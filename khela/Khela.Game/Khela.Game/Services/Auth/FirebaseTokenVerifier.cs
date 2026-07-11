using System.Collections;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;

namespace Khela.Game.Services.Auth
{
    /// <summary>
    /// A verified external identity extracted from a Firebase ID token. Firebase is the single broker for
    /// every provider (Google/Facebook/Apple/guest), so <see cref="Provider"/> tells us which one was used
    /// while <see cref="Uid"/> is the stable, provider-independent key we link accounts on.
    /// </summary>
    public sealed record ExternalIdentity(
        string Uid,
        string Email,
        bool EmailVerified,
        string Name,
        string Picture,
        string Provider);   // "google.com" | "facebook.com" | "apple.com" | "password" | "anonymous" | ...

    public interface IFirebaseTokenVerifier
    {
        /// <summary>True once the Firebase Admin SDK is initialised (credentials configured).</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Verifies a Firebase ID token (signature, issuer, audience, expiry) against the project and
        /// returns the caller's identity. Throws <see cref="FirebaseAuthException"/> on an invalid token
        /// and <see cref="InvalidOperationException"/> if Firebase is not configured on this server.
        /// </summary>
        Task<ExternalIdentity> VerifyAsync(string idToken, CancellationToken ct = default);
    }

    /// <summary>
    /// Wraps FirebaseAdmin's <see cref="FirebaseAuth.VerifyIdTokenAsync(string)"/>. Registered as a singleton;
    /// the FirebaseApp is created once at construction from a service-account credential.
    /// </summary>
    public sealed class FirebaseTokenVerifier : IFirebaseTokenVerifier
    {
        private readonly FirebaseAuth _auth;
        private readonly ILogger<FirebaseTokenVerifier> _logger;

        public bool IsConfigured => _auth != null;

        public FirebaseTokenVerifier(IConfiguration config, ILogger<FirebaseTokenVerifier> logger)
        {
            _logger = logger;

            var projectId = config["Firebase:ProjectId"];
            var credentialsPath = config["Firebase:CredentialsPath"];

            try
            {
                // Reuse the default app if something else already created it (e.g. Crashlytics tooling).
                var app = FirebaseApp.DefaultInstance;
                if (app == null)
                {
                    GoogleCredential credential;
                    if (!string.IsNullOrWhiteSpace(credentialsPath) && File.Exists(credentialsPath))
                        credential = GoogleCredential.FromFile(credentialsPath);
                    else
                        // Falls back to GOOGLE_APPLICATION_CREDENTIALS / workload identity if present.
                        credential = GoogleCredential.GetApplicationDefault();

                    app = FirebaseApp.Create(new AppOptions
                    {
                        Credential = credential,
                        ProjectId = projectId
                    });
                }

                _auth = FirebaseAuth.GetAuth(app);
                _logger.LogInformation("Firebase Auth verifier initialised (project '{ProjectId}').", projectId);
            }
            catch (Exception ex)
            {
                // Never crash startup over social auth — the endpoint reports "not configured" until credentials
                // are supplied. Password/device login keeps working regardless.
                _auth = null;
                _logger.LogWarning(ex,
                    "Firebase Auth verifier NOT initialised — social sign-in disabled. Set Firebase:CredentialsPath " +
                    "(service-account JSON) and Firebase:ProjectId to enable it.");
            }
        }

        public async Task<ExternalIdentity> VerifyAsync(string idToken, CancellationToken ct = default)
        {
            if (_auth == null)
                throw new InvalidOperationException("Firebase Auth is not configured on this server.");
            if (string.IsNullOrWhiteSpace(idToken))
                throw new ArgumentException("Missing Firebase ID token.", nameof(idToken));

            var token = await _auth.VerifyIdTokenAsync(idToken, ct);

            var claims = token.Claims;
            string GetString(string key) =>
                claims != null && claims.TryGetValue(key, out var v) && v != null ? v.ToString() : string.Empty;

            bool emailVerified = claims != null
                && claims.TryGetValue("email_verified", out var ev)
                && ev is bool b && b;

            return new ExternalIdentity(
                Uid: token.Uid,
                Email: GetString("email"),
                EmailVerified: emailVerified,
                Name: GetString("name"),
                Picture: GetString("picture"),
                Provider: ExtractProvider(claims));
        }

        /// <summary>
        /// The sign-in provider lives at claims["firebase"]["sign_in_provider"]. FirebaseAdmin surfaces the
        /// nested object as an IDictionary; read it defensively so an unexpected shape never throws.
        /// </summary>
        private static string ExtractProvider(IReadOnlyDictionary<string, object> claims)
        {
            if (claims == null || !claims.TryGetValue("firebase", out var firebaseClaim) || firebaseClaim == null)
                return string.Empty;

            if (firebaseClaim is IDictionary<string, object> generic
                && generic.TryGetValue("sign_in_provider", out var p) && p != null)
                return p.ToString();

            // Fallback for a non-generic IDictionary shape.
            if (firebaseClaim is IDictionary dict && dict.Contains("sign_in_provider"))
                return dict["sign_in_provider"]?.ToString() ?? string.Empty;

            return string.Empty;
        }
    }
}
