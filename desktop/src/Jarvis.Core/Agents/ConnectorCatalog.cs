namespace Jarvis.Core.Agents;

/// <summary>How Jarvis gets access to a service in the catalog.</summary>
public enum CatalogSignIn
{
    /// <summary>Browser sign-in that Jarvis runs itself (OAuth with dynamic client registration and PKCE).</summary>
    Browser,

    /// <summary>A token the user creates on the service's site and pastes once (GitHub: one per account).</summary>
    Token,
}

/// <summary>A service Jarvis knows how to connect: its official MCP server and how to sign in to it.</summary>
/// <param name="Id">Becomes the connector's name (and, for another account, "id-label").</param>
/// <param name="TokenPage">Where to make the token, for <see cref="CatalogSignIn.Token"/>.</param>
/// <param name="TokenHint">What to choose on that page.</param>
public sealed record CatalogService(string Id, string Name, string Category, string Description, string Url, CatalogSignIn SignIn, string? TokenPage = null, string? TokenHint = null);

/// <summary>
/// The services with official MCP servers that Jarvis connects in one step. Every one was checked
/// to answer at its address and (for browser sign-in) to let a new app register itself, so Jarvis
/// signs in as itself: the access belongs to Jarvis, kept in Windows-protected storage, and works
/// with whichever AI Jarvis runs on. Others (Slack, HubSpot, Asana, Box) don't allow that yet; a
/// custom connector covers any server with a key.
/// </summary>
public static class ConnectorCatalog
{
    public static IReadOnlyList<CatalogService> All { get; } =
    [
        new("github", "GitHub", "Code", "Repositories, issues, pull requests and code search.", "https://api.githubcopilot.com/mcp/", CatalogSignIn.Token,
            "https://github.com/settings/personal-access-tokens/new",
            "Sign in to GitHub as the account you want, make a fine-grained token, pick the repositories, and give it the permissions Jarvis may use (for example Contents, Issues and Pull requests). Another account? Sign in as it and make another token."),
        new("notion", "Notion", "Work", "Pages, databases and comments in your workspace.", "https://mcp.notion.com/mcp", CatalogSignIn.Browser),
        new("linear", "Linear", "Work", "Issues, projects and cycles.", "https://mcp.linear.app/mcp", CatalogSignIn.Browser),
        new("atlassian", "Jira & Confluence", "Work", "Atlassian issues and pages.", "https://mcp.atlassian.com/v1/mcp", CatalogSignIn.Browser),
        new("monday", "monday.com", "Work", "Boards, items and updates.", "https://mcp.monday.com/mcp", CatalogSignIn.Browser),
        new("clickup", "ClickUp", "Work", "Tasks, lists and docs.", "https://mcp.clickup.com/mcp", CatalogSignIn.Browser),
        new("airtable", "Airtable", "Data", "Bases, tables and records.", "https://mcp.airtable.com/mcp", CatalogSignIn.Browser),
        new("supabase", "Supabase", "Data", "Projects, databases, SQL and logs.", "https://mcp.supabase.com/mcp", CatalogSignIn.Browser),
        new("neon", "Neon", "Data", "Serverless Postgres projects and queries.", "https://mcp.neon.tech/mcp", CatalogSignIn.Browser),
        new("prisma", "Prisma Postgres", "Data", "Prisma Postgres databases.", "https://mcp.prisma.io/mcp", CatalogSignIn.Browser),
        new("sentry", "Sentry", "Code", "Errors, issues and releases.", "https://mcp.sentry.dev/mcp", CatalogSignIn.Browser),
        new("vercel", "Vercel", "Code", "Projects, deployments and logs.", "https://mcp.vercel.com", CatalogSignIn.Browser),
        new("cloudflare", "Cloudflare", "Code", "Workers, DNS and account resources.", "https://mcp.cloudflare.com/mcp", CatalogSignIn.Browser),
        new("figma", "Figma", "Design", "Design files and components.", "https://mcp.figma.com/mcp", CatalogSignIn.Browser),
        new("canva", "Canva", "Design", "Designs, templates and exports.", "https://mcp.canva.com/mcp", CatalogSignIn.Browser),
        new("webflow", "Webflow", "Design", "Sites, pages and CMS collections.", "https://mcp.webflow.com/mcp", CatalogSignIn.Browser),
        new("wix", "Wix", "Design", "Sites and their content.", "https://mcp.wix.com/mcp", CatalogSignIn.Browser),
        new("dropbox", "Dropbox", "Files", "Files and folders.", "https://mcp.dropbox.com/mcp", CatalogSignIn.Browser),
        new("stripe", "Stripe", "Payments", "Customers, payments and invoices.", "https://mcp.stripe.com", CatalogSignIn.Browser),
        new("paypal", "PayPal", "Payments", "Invoices, orders and transactions.", "https://mcp.paypal.com/mcp", CatalogSignIn.Browser),
        new("square", "Square", "Payments", "Payments, orders and catalog.", "https://mcp.squareup.com/mcp", CatalogSignIn.Browser),
        new("intercom", "Intercom", "Work", "Conversations and contacts.", "https://mcp.intercom.com/mcp", CatalogSignIn.Browser),
    ];

    /// <summary>The catalog service a connector came from (by its URL), or null for a custom one.</summary>
    public static CatalogService? Of(Connector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        return All.FirstOrDefault(service => string.Equals(service.Url, connector.Url, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The connector name for one more account of a service: its id, then id-label (the label the
    /// user gave, or the account's own login), numbered if that's taken.
    /// </summary>
    public static string NameFor(CatalogService service, string? label, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(service);
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleaned = new string((label ?? string.Empty).Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var first = cleaned.Length == 0 ? service.Id : $"{service.Id}-{cleaned}";
        first = first.Length > 40 ? first[..40].TrimEnd('-') : first;
        if (!used.Contains(first))
        {
            return first;
        }

        for (var n = 2; ; n++)
        {
            var numbered = $"{first}-{n}";
            if (!used.Contains(numbered))
            {
                return numbered;
            }
        }
    }
}
