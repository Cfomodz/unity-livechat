using LiveChat.Obs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LiveChat.Tests
{
    /// <summary>The engine-free parts of OBS control: obs-websocket v5 messages, auth, and the command rules.</summary>
    public class ObsTests
    {
        // The example salt and challenge from obs-websocket's protocol.md (Hello / Authentication)
        private const string DocSalt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
        private const string DocChallenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

        [Test]
        public void AuthenticationMatchesAnIndependentSha256Computation()
        {
            // Expected values computed separately with openssl:
            //   printf '%s' 'supersecretpassword<salt>' | openssl dgst -sha256 -binary | base64
            //     -> H1IfVz1pSREUQzbFTVnX/Tyb+gMhMik5x7yUBCY0PTs=  (the base64 secret)
            //   printf '%s' '<secret><challenge>' | openssl dgst -sha256 -binary | base64
            Assert.AreEqual("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=",
                ObsProtocol.ComputeAuthentication("supersecretpassword", DocSalt, DocChallenge));
        }

        [Test]
        public void AuthenticationDependsOnEveryInput()
        {
            string auth = ObsProtocol.ComputeAuthentication("supersecretpassword", DocSalt, DocChallenge);
            Assert.AreNotEqual(auth, ObsProtocol.ComputeAuthentication("supersecretpasswordX", DocSalt, DocChallenge));
            Assert.AreNotEqual(auth, ObsProtocol.ComputeAuthentication("supersecretpassword", DocChallenge, DocSalt));
        }

        [Test]
        public void IdentifyIncludesAuthOnlyWhenTheHelloAsksForIt()
        {
            ObsHello withAuth = ObsProtocol.ParseHello(JObject.Parse(
                "{\"obsWebSocketVersion\":\"5.5.2\",\"rpcVersion\":1,\"authentication\":{\"challenge\":\"" + DocChallenge + "\",\"salt\":\"" + DocSalt + "\"}}"));
            Assert.IsTrue(withAuth.AuthenticationRequired);
            Assert.AreEqual("5.5.2", withAuth.ObsWebSocketVersion);

            ObsMessage identify = ObsProtocol.Parse(ObsProtocol.BuildIdentify(withAuth, "supersecretpassword",
                ObsEventSubscription.General | ObsEventSubscription.Scenes | ObsEventSubscription.Outputs));
            Assert.AreEqual(ObsOpCode.Identify, identify.Op);
            Assert.AreEqual(1, (int)identify.Data["rpcVersion"]);
            Assert.AreEqual(1 | 4 | 64, (int)identify.Data["eventSubscriptions"]);
            Assert.AreEqual("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", (string)identify.Data["authentication"]);

            ObsHello noAuth = ObsProtocol.ParseHello(JObject.Parse("{\"obsWebSocketVersion\":\"5.0.0\",\"rpcVersion\":1}"));
            Assert.IsFalse(noAuth.AuthenticationRequired);
            ObsMessage plain = ObsProtocol.Parse(ObsProtocol.BuildIdentify(noAuth, "secret", ObsEventSubscription.Outputs));
            Assert.IsNull(plain.Data["authentication"]);
        }

        [Test]
        public void IdentifyNeverContainsThePassword()
        {
            ObsHello hello = new ObsHello { RpcVersion = 1, Challenge = DocChallenge, Salt = DocSalt };
            StringAssert.DoesNotContain("hunter2pass", ObsProtocol.BuildIdentify(hello, "hunter2pass", ObsEventSubscription.None));
        }

        [Test]
        public void RequestsCarryTypeIdAndData()
        {
            ObsMessage request = ObsProtocol.Parse(ObsProtocol.BuildRequest("SetCurrentProgramScene", "dd-7",
                new JObject { ["sceneName"] = "Game" }));
            Assert.AreEqual(ObsOpCode.Request, request.Op);
            Assert.AreEqual("SetCurrentProgramScene", (string)request.Data["requestType"]);
            Assert.AreEqual("dd-7", (string)request.Data["requestId"]);
            Assert.AreEqual("Game", (string)request.Data["requestData"]["sceneName"]);

            ObsMessage bare = ObsProtocol.Parse(ObsProtocol.BuildRequest("GetStreamStatus", "dd-8"));
            Assert.IsNull(bare.Data["requestData"]);
        }

        [Test]
        public void ParsesResponsesAndTheirFailureReasons()
        {
            ObsMessage ok = ObsProtocol.Parse("{\"op\":7,\"d\":{\"requestType\":\"GetStreamStatus\",\"requestId\":\"dd-1\","
                + "\"requestStatus\":{\"result\":true,\"code\":100},\"responseData\":{\"outputActive\":true}}}");
            Assert.AreEqual(ObsOpCode.RequestResponse, ok.Op);
            ObsResponse response = ObsProtocol.ParseResponse(ok.Data);
            Assert.IsTrue(response.Ok);
            Assert.AreEqual("dd-1", response.RequestId);
            Assert.IsTrue((bool)response.Data["outputActive"]);

            ObsResponse running = ObsProtocol.ParseResponse(ObsProtocol.Parse("{\"op\":7,\"d\":{\"requestType\":\"StartStream\","
                + "\"requestId\":\"dd-2\",\"requestStatus\":{\"result\":false,\"code\":500}}}").Data);
            Assert.IsFalse(running.Ok);
            Assert.AreEqual(ObsProtocol.StatusOutputRunning, running.Code);
            Assert.AreEqual("OutputRunning (500)", running.Reason);

            ObsResponse missing = ObsProtocol.ParseResponse(ObsProtocol.Parse("{\"op\":7,\"d\":{\"requestType\":\"SetCurrentProgramScene\","
                + "\"requestId\":\"dd-3\",\"requestStatus\":{\"result\":false,\"code\":600,\"comment\":\"No source was found by the name of `Nope`.\"}}}").Data);
            Assert.AreEqual("No source was found by the name of `Nope`.", missing.Reason);
        }

        [Test]
        public void ParsesEventsAndRejectsGarbage()
        {
            ObsMessage message = ObsProtocol.Parse("{\"op\":5,\"d\":{\"eventType\":\"StreamStateChanged\",\"eventIntent\":64,"
                + "\"eventData\":{\"outputActive\":true,\"outputState\":\"OBS_WEBSOCKET_OUTPUT_STARTED\"}}}");
            Assert.AreEqual(ObsOpCode.Event, message.Op);
            (string type, JObject data) = ObsProtocol.ParseEvent(message.Data);
            Assert.AreEqual("StreamStateChanged", type);
            Assert.IsTrue((bool)data["outputActive"]);

            Assert.IsNull(ObsProtocol.Parse("not json"));
            Assert.IsNull(ObsProtocol.Parse("{\"d\":{}}"));
            Assert.IsNull(ObsProtocol.Parse("[1,2]"));
            Assert.IsNull(ObsProtocol.Parse(""));
        }

        [Test]
        public void CloseCodesExplainAuthFailures()
        {
            Assert.AreEqual("wrong password", ObsProtocol.CloseReason(4009));
            Assert.IsNull(ObsProtocol.CloseReason(1000));
        }

        [Test]
        public void SceneShortcutsAndNamesResolve()
        {
            string[] scenes = { "Starting Soon", "Deep Dig", "Deep Dig (no cam)", "BRB" };

            Assert.AreEqual("Starting Soon", ObsSceneResolver.Resolve("starting", "Starting Soon", "Deep Dig", scenes, out _));
            Assert.AreEqual("Deep Dig", ObsSceneResolver.Resolve("game", "Starting Soon", "Deep Dig", scenes, out _));
            Assert.AreEqual("Deep Dig", ObsSceneResolver.Resolve("Deep Dig", null, null, scenes, out _), "an exact name beats a partial match");
            Assert.AreEqual("BRB", ObsSceneResolver.Resolve("brb", null, null, scenes, out _));
            Assert.AreEqual("Deep Dig (no cam)", ObsSceneResolver.Resolve("no cam", null, null, scenes, out _));
            Assert.AreEqual("Starting Soon", ObsSceneResolver.Resolve("soon", null, null, scenes, out _));

            Assert.IsNull(ObsSceneResolver.Resolve("dig", null, null, scenes, out string ambiguous));
            StringAssert.Contains("matches 2 scenes", ambiguous);
            Assert.IsNull(ObsSceneResolver.Resolve("Credits", null, null, scenes, out string missing));
            StringAssert.Contains("No OBS scene", missing);
            Assert.IsNull(ObsSceneResolver.Resolve("  ", "Starting Soon", null, scenes, out string empty));
            StringAssert.Contains("starting", empty);
            // Without a configured game scene, "game" is just a name to look for
            Assert.IsNull(ObsSceneResolver.Resolve("game", null, null, scenes, out _));
        }

        [Test]
        public void EndingTheStreamNeedsConfirmation()
        {
            ObsStopConfirmation confirm = new ObsStopConfirmation(15);

            Assert.AreEqual(ObsStopConfirmation.Outcome.Armed, confirm.Check("", 100));
            Assert.AreEqual(ObsStopConfirmation.Outcome.Confirmed, confirm.Check("", 110), "a second stop request within the window");
            Assert.AreEqual(ObsStopConfirmation.Outcome.Armed, confirm.Check("", 111), "confirming disarms");

            Assert.AreEqual(ObsStopConfirmation.Outcome.Armed, confirm.Check("", 200));
            Assert.AreEqual(ObsStopConfirmation.Outcome.Armed, confirm.Check("", 216), "too late: arms again instead");

            Assert.AreEqual(ObsStopConfirmation.Outcome.Cancelled, confirm.Check("cancel", 217));
            Assert.AreEqual(ObsStopConfirmation.Outcome.Armed, confirm.Check("", 218), "cancel disarms");

            ObsStopConfirmation direct = new ObsStopConfirmation(15);
            Assert.AreEqual(ObsStopConfirmation.Outcome.Confirmed, direct.Check("CONFIRM", 5), "confirm is explicit");
        }

        [Test]
        public void StatusLineIsShortAndSafe()
        {
            Assert.AreEqual("connected, live, scene Game",
                ObsController.FormatStatus(ObsConnectionState.Ready, false, true, true, false, "Game"));
            Assert.AreEqual("connected, not live, scene Starting",
                ObsController.FormatStatus(ObsConnectionState.Ready, false, true, false, false, "Starting"));
            Assert.AreEqual("not running (retrying)",
                ObsController.FormatStatus(ObsConnectionState.Waiting, false, false, null, false, null));
            Assert.AreEqual("disconnected (retrying)",
                ObsController.FormatStatus(ObsConnectionState.Waiting, false, true, null, false, null));
            Assert.AreEqual("wrong password (check the OBS settings)",
                ObsController.FormatStatus(ObsConnectionState.Waiting, true, false, null, false, null));
        }

        [Test]
        public void SettingsDefaultToOffAndNeverPrintThePassword()
        {
            ObsSettings settings = UnityEngine.JsonUtility.FromJson<ObsSettings>("{\"password\":\"hunter2pass\"}");
            Assert.IsFalse(settings.enabled);
            Assert.AreEqual("ws://127.0.0.1:4455", settings.Url);
            StringAssert.DoesNotContain("hunter2pass", settings.ToString());
        }

        [Test]
        public void ChatCommandNamesFollowThePrefix()
        {
            ObsChatCommands commands = new ObsChatCommands(null, "!DD");

            Assert.AreEqual("ddscene", commands.Scene);
            Assert.AreEqual("ddgolive", commands.GoLive);
            Assert.AreEqual("ddend", commands.End);
            Assert.AreEqual("ddobs", commands.Status);
            Assert.IsTrue(commands.Handles("!DDGOLIVE"));
            Assert.IsFalse(commands.Handles("ddgo"));
            Assert.IsNull(commands.Run("ddobs", "", 0).Result, "without OBS control there's nothing to run");
        }
    }
}
