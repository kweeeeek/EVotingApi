namespace EVoting.Api.Security
{
    /// <summary>
    /// Keeps the text of the deployment template from being used where a secret belongs.
    /// <para>
    /// <c>deploy/appsettings.example.json</c> writes every secret as <c>&lt;set via EVOTING_...&gt;</c> so that nobody mistakes the line for a
    /// real value. If such a file is copied to <c>appsettings.json</c> and the environment variable is forgotten, the API would otherwise read
    /// that text as the value: a JWT signing key that is public (the text is longer than the 32-character minimum), a kiosk secret that is
    /// public, a pepper that cannot verify any password. Start-up therefore refuses a setting that still holds the template text, and the
    /// message names the setting and never its value.
    /// </para>
    /// </summary>
    public static class SecretSettings
    {
        /// <summary>The marker that every placeholder of the template carries.</summary>
        public const string PlaceholderMarker = "<set via";

        /// <summary>True when <paramref name="value"/> still holds placeholder text of the deployment template (also inside a larger text such as a connection string).</summary>
        public static bool LooksLikePlaceholder(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Throws when <paramref name="value"/> holds placeholder text. A missing (null or blank) value is not this method's business:
        /// each setting has its own rule for that (the JWT key must exist, a kiosk sign-in without a secret is refused, ...).
        /// </summary>
        /// <param name="settingName">The environment variable and/or configuration key, for the message.</param>
        public static void RejectPlaceholder(string settingName, string? value)
        {
            if (LooksLikePlaceholder(value))
                throw new InvalidOperationException(
                    $"{settingName} still holds the text of the deployment template. Set the real value (preferably as an environment variable on the server) before starting the API.");
        }
    }
}
