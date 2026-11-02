using Sentry_Crashes_Deobfuscator.Controllers;
using Xunit;

namespace Deobfuscator.Tests
{
    public class ClientKeyAuthTests
    {
        private const string Api = "api-secret";
        private const string Client = "c9d30ea1bd6d706be8dfcc98ea15e9bd";

        [Fact]
        public void Bearer_ApiKey_Accepted() =>
            Assert.True(SentryDeobfuscationController.IsAuthorized("Bearer " + Api, null, null, Api, Client));

        [Fact]
        public void XSentryAuth_ClientKey_Accepted() =>
            Assert.True(SentryDeobfuscationController.IsAuthorized(null,
                $"Sentry sentry_version=7, sentry_key={Client}, sentry_client=sentry.dotnet/5.14.1", null, Api, Client));

        [Fact]
        public void QueryClientKey_Accepted() =>
            Assert.True(SentryDeobfuscationController.IsAuthorized(null, null, Client, Api, Client));

        [Fact]
        public void WrongKey_Rejected() =>
            Assert.False(SentryDeobfuscationController.IsAuthorized(null, "Sentry sentry_key=wrong", null, Api, Client));

        [Fact]
        public void ClientKeyNotConfigured_Rejected() =>
            Assert.False(SentryDeobfuscationController.IsAuthorized(null, "Sentry sentry_key=anything", null, Api, null));
    }
}
