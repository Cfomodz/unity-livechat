using LiveChat.Twitch;
using NUnit.Framework;

namespace LiveChat.Tests
{
    public class ChatSendQueueTests
    {
        [Test]
        public void SendsAtMostOnePerSecondWhenNotPrivileged()
        {
            ChatSendQueue queue = new ChatSendQueue();
            queue.Enqueue("a", null, 0);
            queue.Enqueue("b", null, 0);

            Assert.IsTrue(queue.TryDequeue(0, out ChatSendQueue.Outgoing first));
            Assert.AreEqual("a", first.Text);
            Assert.IsFalse(queue.TryDequeue(0.5, out _));
            Assert.IsTrue(queue.TryDequeue(1.1, out ChatSendQueue.Outgoing second));
            Assert.AreEqual("b", second.Text);
        }

        [Test]
        public void StaysUnderTwentyPerThirtySeconds()
        {
            ChatSendQueue queue = new ChatSendQueue { MaxQueued = 100 };
            for (int i = 0; i < 40; i++)
                queue.Enqueue($"message {i}", null, 0);

            int sent = 0;
            for (double t = 0; t < 29.9; t += 1.1)
                if (queue.TryDequeue(t, out _))
                    sent++;
            Assert.LessOrEqual(sent, 20);
            Assert.Greater(sent, 10);

            Assert.IsTrue(queue.TryDequeue(31, out _), "The window slides");
        }

        [Test]
        public void PrivilegedBotsSendFaster()
        {
            ChatSendQueue queue = new ChatSendQueue { Privileged = true, MaxQueued = 100 };
            for (int i = 0; i < 50; i++)
                queue.Enqueue($"message {i}", null, 0);
            int sent = 0;
            for (double t = 0; t < 10; t += 0.2)
                if (queue.TryDequeue(t, out _))
                    sent++;
            Assert.AreEqual(50, sent);
        }

        [Test]
        public void DropsRecentDuplicates()
        {
            ChatSendQueue queue = new ChatSendQueue();
            Assert.IsTrue(queue.Enqueue("A run is already in progress.", null, 0));
            Assert.IsFalse(queue.Enqueue("A run is already in progress.", null, 5));
            Assert.AreEqual(1, queue.Count);
            Assert.IsTrue(queue.Enqueue("A run is already in progress.", null, 40), "Allowed again after the window");
        }

        [Test]
        public void DropsTheOldestWhenFull()
        {
            ChatSendQueue queue = new ChatSendQueue { MaxQueued = 2 };
            queue.Enqueue("1", null, 0);
            queue.Enqueue("2", null, 0);
            queue.Enqueue("3", "parent", 0);
            Assert.AreEqual(2, queue.Count);
            Assert.IsTrue(queue.TryDequeue(0, out ChatSendQueue.Outgoing next));
            Assert.AreEqual("2", next.Text);
            Assert.IsTrue(queue.TryDequeue(2, out next));
            Assert.AreEqual("3", next.Text);
            Assert.AreEqual("parent", next.ReplyToMessageId);
        }

        [Test]
        public void TrimsToTwitchsLengthLimit()
        {
            ChatSendQueue queue = new ChatSendQueue();
            queue.Enqueue(new string('x', 600), null, 0);
            Assert.IsTrue(queue.TryDequeue(0, out ChatSendQueue.Outgoing message));
            Assert.AreEqual(ChatSendQueue.MaxLength, message.Text.Length);
        }

        [Test]
        public void IgnoresBlankMessages()
        {
            ChatSendQueue queue = new ChatSendQueue();
            Assert.IsFalse(queue.Enqueue("   ", null, 0));
            Assert.AreEqual(0, queue.Count);
        }
    }
}
