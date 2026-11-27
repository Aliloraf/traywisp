using System.Collections.Immutable;

namespace TrayWisp.Core.Output;

/// <summary>
/// An action a plugin requests for a menu item: <see cref="RefreshAction"/>, <see cref="UrlAction"/> or
/// <see cref="CommandAction"/>. Actions are inert, untrusted data. The parser never runs, opens, resolves or
/// fetches anything, and no action may be carried out without the plugin trust checks (Phase 3) and the
/// execution safeguards (Phase 4) described in docs/security.md.
/// </summary>
public abstract class OutputAction
{
    private protected OutputAction(bool refreshesPlugin)
    {
        RefreshesPlugin = refreshesPlugin;
    }

    /// <summary>
    /// <see langword="true"/> when the plugin that produced this action should be run again afterwards
    /// (<c>refresh=true</c>): immediately for a <see cref="RefreshAction"/>, after the URL has been handed
    /// off for a <see cref="UrlAction"/>, and after the command has completed for a <see cref="CommandAction"/>.
    /// Never a request to refresh other plugins.
    /// </summary>
    public bool RefreshesPlugin { get; }
}

/// <summary><c>refresh=true</c> without a URL or command: run the producing plugin again.</summary>
public sealed class RefreshAction : OutputAction
{
    private RefreshAction()
        : base(refreshesPlugin: true)
    {
    }

    internal static RefreshAction Instance { get; } = new();
}

/// <summary><c>href=...</c>: open an absolute HTTP or HTTPS URL.</summary>
public sealed class UrlAction : OutputAction
{
    internal UrlAction(Uri url, bool refreshesPlugin)
        : base(refreshesPlugin)
    {
        Url = url;
    }

    /// <summary>
    /// The validated URL. Its scheme is <c>http</c> or <c>https</c> and it has a host. <see cref="Uri.OriginalString"/>
    /// is exactly the text the plugin wrote, and it is a well-formed URI: every percent escape, including an
    /// escaped control character such as <c>%0A</c>, is kept as written. <see cref="Uri.AbsoluteUri"/> keeps
    /// escaped control characters escaped too. <see cref="Uri.ToString"/> unescapes some characters and must
    /// not be used to open the URL.
    /// </summary>
    public Uri Url { get; }
}

/// <summary>
/// <c>shell=...</c> (or <c>bash=...</c>) with optional <c>paramN=...</c> arguments: a request to start an
/// executable with an explicit argument list. Nothing here implies evaluation by a shell.
/// </summary>
public sealed class CommandAction : OutputAction
{
    internal CommandAction(string executable, ImmutableArray<string> arguments, bool? runInTerminal, bool refreshesPlugin)
        : base(refreshesPlugin)
    {
        Executable = executable;
        Arguments = arguments;
        RunInTerminal = runInTerminal;
    }

    /// <summary>The executable exactly as written. It is not split, resolved, expanded or checked for existence.</summary>
    public string Executable { get; }

    /// <summary>The arguments in index order, exactly as written. Empty strings are preserved.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>
    /// The <c>terminal</c> preference: <see langword="true"/> to run in a visible terminal,
    /// <see langword="false"/> to run in the background, <see langword="null"/> when not specified. Only a
    /// request; the parser launches nothing.
    /// </summary>
    public bool? RunInTerminal { get; }
}
