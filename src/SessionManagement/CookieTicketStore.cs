using System.Threading.Tasks;
using IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;

namespace Duende.Bff
{
    public class CookieTicketStore : ITicketStore
    {
        private readonly IUserSessionStore _store;
        private readonly ILogger<CookieTicketStore> _logger;

        public CookieTicketStore(
            IUserSessionStore store,
            ILogger<CookieTicketStore> logger)
        {
            _store = store;
            _logger = logger;
        }

        public async Task<string> StoreAsync(AuthenticationTicket ticket)
        {
            var key = CryptoRandom.CreateUniqueId(format: CryptoRandom.OutputFormat.Hex);

            var session = new UserSession
            {
                Key = key,
                Created = ticket.GetIssued(),
                Renewed = ticket.GetIssued(),
                Expires = ticket.GetExpiration(),
                SubjectId = ticket.GetSubjectId(),
                SessionId = ticket.GetSessionId(),
                Scheme = ticket.AuthenticationScheme,
                Ticket = ticket.Serialize(),
            };

            await _store.CreateUserSessionAsync(session);

            return key;
        }

        public async Task<AuthenticationTicket> RetrieveAsync(string key)
        {
            var session = await _store.GetUserSessionAsync(key);
            if (session != null)
            {
                var ticket = session.Deserialize();
                if (ticket == null)
                {
                    // if we failed to get a ticket, then remove DB record 
                    _logger.LogWarning("Failed to deserialize authentication ticket from store, deleting record for key {key}", key);
                    await RemoveAsync(key);
                }

                return ticket;
            }

            return null;
        }

        public async Task RenewAsync(string key, AuthenticationTicket ticket)
        {
            var session = await _store.GetUserSessionAsync(key);
            if (session != null)
            {
                session.Renewed = ticket.GetIssued();
                session.Expires = ticket.GetExpiration();
                session.Ticket = ticket.Serialize();

                // todo: discuss updating sub and sid?
                
                await _store.UpdateUserSessionAsync(session);
            }
            else
            {
                _logger.LogWarning("No record found in user session store when trying to renew authentication ticket for key {key} and subject {subjectId}", key, ticket.GetSubjectId());
            }
        }

        public Task RemoveAsync(string key)
        {
            return _store.DeleteUserSessionAsync(key);
        }
    }
}