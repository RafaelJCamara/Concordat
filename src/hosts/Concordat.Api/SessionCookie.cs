namespace Concordat.Api;

/// <summary>
/// The browser-held half of a session (decision 26).
/// </summary>
/// <remarks>
/// <para>
/// <b>The SPA keeps its credential in memory and this cookie is how it gets one back after a
/// reload.</b> A token in <c>localStorage</c> is readable by any script that runs on the page,
/// and this project has already declined to ship one XSS hole (ADR-006) — so the credential is
/// never written anywhere script can read. An <c>HttpOnly</c> cookie is the one store a browser
/// offers that script cannot touch.
/// </para>
/// <para>
/// <b>Exactly one route accepts it: <c>POST /v1/auth/resume</c>.</b> That is what makes CSRF
/// structurally impossible rather than merely mitigated. A cookie that authenticated ordinary
/// requests would let any page on the internet trigger a state change in the browser's name;
/// this one's entire power is handing back a credential the browser already holds, and every
/// mutating route still requires an <c>Authorization</c> header that a cross-site request cannot
/// set. <c>SameSite=Strict</c> is then a second lock on a door that is already bolted.
/// </para>
/// </remarks>
public static class SessionCookie
{
    /// <summary>The cookie name.</summary>
    /// <remarks>
    /// The <c>__Host-</c> prefix is deliberately not used. It would be stronger — browsers
    /// enforce Secure, path <c>/</c> and no Domain — but it also makes the cookie unusable over
    /// plain HTTP, and the ordinary evaluation path for this product is a container reached at
    /// <c>http://localhost:5062</c>. A security feature that stops the quickstart working gets
    /// turned off rather than obeyed.
    /// </remarks>
    public const string Name = "concordat_session";

    /// <summary>Issues the cookie alongside a freshly minted session credential.</summary>
    /// <param name="http">The request context.</param>
    /// <param name="credential">The session API key.</param>
    /// <param name="expiresAt">When the key stops working.</param>
    public static void Issue(HttpContext http, string credential, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.Cookies.Append(Name, credential, Options(http, expiresAt));
    }

    /// <summary>Removes the cookie.</summary>
    /// <param name="http">The request context.</param>
    /// <remarks>
    /// Needed as a server round trip because script cannot delete an <c>HttpOnly</c> cookie —
    /// the property that makes it safe is the same one that makes signing out require the API.
    /// </remarks>
    public static void Clear(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // Deleted with the same attributes it was written with. A browser matches on name, path,
        // domain and secure flag; a mismatch leaves the original cookie in place and sign-out
        // silently does nothing.
        http.Response.Cookies.Delete(Name, Options(http, DateTimeOffset.UnixEpoch));
    }

    private static CookieOptions Options(HttpContext http, DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,

        // Follows the request rather than being hard-coded on. Forcing Secure would make the
        // cookie silently not-set on a self-hosted registry served over plain HTTP, and a
        // sign-in that appears to work while the reload keeps failing is a worse outcome than an
        // unencrypted cookie on a deployment that already chose not to encrypt anything.
        Secure = http.Request.IsHttps,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };
}
