using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LiveChat.Twitch
{
    /// <summary>
    /// One Twitch login (the bot, or the broadcaster): loads its saved token, validates and refreshes
    /// it, and falls back to a device code login when there is no usable token.
    /// </summary>
    public class TwitchAccount
    {
        private readonly string _clientId;
        private readonly TwitchTokenStore _store;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private TwitchToken _token;

        public TwitchAccount(string role, string clientId, IEnumerable<string> scopes, TwitchTokenStore store)
        {
            Role = role;
            _clientId = clientId;
            Scopes = scopes.ToArray();
            _store = store;
        }

        public string Role { get; }
        public IReadOnlyList<string> Scopes { get; }
        /// <summary>If set, only this login is accepted (compared case-insensitively).</summary>
        public string ExpectedLogin { get; set; }
        public string UserId => _token?.UserId;
        public string Login => _token?.Login;
        public bool IsAuthorized => _token != null;

        /// <summary>Raised (on the caller's context) when the user needs to enter a code.</summary>
        public event Action<TwitchDeviceAuthorization> AuthorizationRequired;
        public event Action AuthorizationCompleted;

        /// <summary>
        /// Makes sure there is a valid token with the right scopes for the right user, logging in
        /// with a device code if needed. Waits for the user as long as it takes, until cancelled.
        /// </summary>
        public async Task AuthorizeAsync(CancellationToken ct)
        {
            TwitchToken saved = _store.Load(Role);
            if (saved != null && saved.HasScopes(Scopes))
            {
                if (await TryUseAsync(saved, ct))
                    return;
                TwitchToken refreshed = await TwitchOAuth.RefreshAsync(_clientId, saved.RefreshToken, ct);
                if (refreshed != null && await TryUseAsync(refreshed, ct))
                    return;
            }

            _token = null;
            _store.Delete(Role);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TwitchOAuth.DeviceCode code = await TwitchOAuth.RequestDeviceCodeAsync(_clientId, Scopes, ct);
                AuthorizationRequired?.Invoke(new TwitchDeviceAuthorization
                {
                    Role = Role,
                    ExpectedLogin = ExpectedLogin,
                    UserCode = code.UserCode,
                    VerificationUri = code.VerificationUri,
                    ExpiresAtUtc = code.ExpiresAtUtc
                });

                TwitchToken token = await TwitchOAuth.PollDeviceCodeAsync(_clientId, Scopes, code, ct);
                if (token == null)
                    continue; // The code expired unused: show a fresh one
                if (await TryUseAsync(token, ct))
                {
                    AuthorizationCompleted?.Invoke();
                    return;
                }
                // Wrong account: TryUse logged why. Ask again.
            }
        }

        /// <summary>A current access token, refreshed first if it's about to expire.</summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            TwitchToken token = _token ?? throw new TwitchAuthException($"The {Role} account isn't logged in.");
            if (token.ExpiresAtUtc - DateTime.UtcNow < TimeSpan.FromMinutes(5))
                await RefreshAsync(token.AccessToken, ct);
            return _token?.AccessToken ?? throw new TwitchAuthException($"The {Role} account's login expired.");
        }

        /// <summary>
        /// Refreshes the token unless another caller already replaced <paramref name="staleAccessToken"/>.
        /// Returns false if Twitch rejected the refresh token, so the account must log in again.
        /// </summary>
        public async Task<bool> RefreshAsync(string staleAccessToken, CancellationToken ct)
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                if (_token == null)
                    return false;
                if (_token.AccessToken != staleAccessToken)
                    return true;
                TwitchToken refreshed = await TwitchOAuth.RefreshAsync(_clientId, _token.RefreshToken, ct);
                if (refreshed == null || !await TryUseAsync(refreshed, ct))
                {
                    _token = null;
                    _store.Delete(Role);
                    return false;
                }
                return true;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>The hourly validation Twitch requires. False means the account must log in again.</summary>
        public async Task<bool> ValidateAsync(CancellationToken ct)
        {
            TwitchToken token = _token;
            if (token == null)
                return false;
            if (await TwitchOAuth.ValidateAsync(token.AccessToken, ct) != null)
                return true;
            return await RefreshAsync(token.AccessToken, ct);
        }

        /// <summary>Validates a token and, if it belongs to an acceptable user, saves and uses it.</summary>
        private async Task<bool> TryUseAsync(TwitchToken token, CancellationToken ct)
        {
            TwitchOAuth.Validation validation = await TwitchOAuth.ValidateAsync(token.AccessToken, ct);
            if (validation == null)
                return false;
            if (!string.IsNullOrEmpty(validation.ClientId) && validation.ClientId != _clientId)
                return false;
            if (!string.IsNullOrEmpty(ExpectedLogin) && !string.Equals(validation.Login, ExpectedLogin, StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Debug.LogWarning($"[LiveChat] The {Role} login must be {ExpectedLogin}, but {validation.Login} logged in. Log in as {ExpectedLogin}.");
                return false;
            }

            token.UserId = validation.UserId;
            token.Login = validation.Login;
            token.Scopes = validation.Scopes;
            token.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(validation.ExpiresInSeconds);
            if (!token.HasScopes(Scopes))
                return false;

            _token = token;
            _store.Save(Role, token);
            return true;
        }
    }
}
