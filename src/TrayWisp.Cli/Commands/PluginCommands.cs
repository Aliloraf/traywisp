using System.Globalization;
using System.Text.Json;
using TrayWisp.Cli.CommandLine;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Cli.Commands;

// list, inspect, approve, revoke and status: thin orchestration of discovery and the trust service.
internal static class PluginCommands
{
    public static CommandResult List(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();

        // Discovery only: no storage is used, so only the root is resolved.
        PluginRoot? root = CommandSupport.ResolveRoot(request, environment, result);
        if (root is null || !CommandSupport.ValidateRoot(root, result, cancellationToken))
        {
            return result;
        }

        PluginDiscoveryResult discovery = PluginDiscovery.Discover(root.Value, PluginDiscoveryOptions.Default, cancellationToken);
        result.AddRange(discovery.Diagnostics.Select(CliDiagnostic.From));
        if (!discovery.IsComplete)
        {
            result.Add(IncompleteScan());
            result.ExitCode = ExitCodes.DiscoveryIncomplete;
            result.Summary = "the scan was incomplete; plugins missing from the list may still exist";
        }

        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", root.Value);
            writer.WriteBoolean("complete", discovery.IsComplete);
            writer.WriteNumber("entriesInspected", discovery.EntriesInspected);
            writer.WriteStartArray("plugins");
            foreach (PluginDescriptor plugin in discovery.Plugins)
            {
                writer.WriteStartObject();
                writer.SafeString("pluginId", plugin.Id.Value);
                writer.SafeString("displayTitle", plugin.DisplayTitle);
                CommandSupport.WriteFileNameJson(writer, plugin.FileName);
                writer.WritePropertyName("metadata");
                CommandSupport.WriteMetadataJson(writer, plugin.Metadata);
                CommandSupport.WriteDiagnosticsJson(writer, "diagnostics", plugin.Diagnostics.Select(CliDiagnostic.From));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin root", root.Value);
            writer.Value("Scan", discovery.IsComplete
                ? string.Create(CultureInfo.InvariantCulture, $"complete, {discovery.Plugins.Length} plugins, {discovery.EntriesInspected} entries inspected")
                : string.Create(CultureInfo.InvariantCulture, $"INCOMPLETE, {discovery.Plugins.Length} plugins listed; plugins missing from this list may still exist"));
            writer.Line("Listing is discovery only: nothing was run, hashed or approved. Use 'traywisp status' for trust checks.");
            foreach (PluginDescriptor plugin in discovery.Plugins)
            {
                writer.Line();
                writer.Field("Plugin", plugin.Id.Value);
                using (writer.Indent())
                {
                    writer.Field("Title", plugin.DisplayTitle, TerminalText.Kind.Label, 512);
                    CommandSupport.WriteFileNameHuman(writer, plugin.FileName);
                    writer.Diagnostics([.. plugin.Diagnostics.Select(CliDiagnostic.From)], "Metadata diagnostics");
                }
            }
        };
        return result;
    }

    public static CommandResult Inspect(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null
            || !CommandSupport.CheckStorageOverlap(paths, result)
            || !CommandSupport.ValidateRoot(paths, result, cancellationToken)
            || !CommandSupport.CheckStorageDirectory(paths.TrustDirectory, "trust store", result, cancellationToken))
        {
            return result;
        }

        // Inspection never reads or changes the trust store, but the service is bound to it and checks the overlap.
        PluginTrustService? trust = CommandSupport.CreateTrustService(paths, environment, result);
        if (trust is null)
        {
            return result;
        }

        PluginId id = request.PluginId!;

        // Metadata comes from discovery, read before the fingerprint and separately from it.
        PluginDiscoveryResult discovery = PluginDiscovery.Discover(paths.Root.Value, PluginDiscoveryOptions.Default, cancellationToken);
        PluginDescriptor? descriptor = discovery.Plugins.FirstOrDefault(plugin => plugin.Id == id);
        PluginInspectionResult inspection = trust.Inspect(paths.Root.Value, id, cancellationToken);
        result.AddRange(CliDiagnostic.From(inspection.Diagnostics));
        if (descriptor is null)
        {
            result.Add(CliDiagnostic.Warning(
                CliDiagnosticCode.PluginNotInDiscovery,
                discovery.IsComplete
                    ? "The plugin was not found by discovery, so no metadata is shown."
                    : "The plugin was not found by an incomplete discovery scan, so no metadata is shown."));
        }

        if (!discovery.IsComplete)
        {
            result.Add(IncompleteScan());
        }

        if (TerminalText.HasNonAscii(id.Value))
        {
            result.Add(Lookalike());
        }

        PluginFingerprint? fingerprint = inspection.Plugin;
        if (inspection.Status != PluginInspectionStatus.Inspected)
        {
            result.ExitCode = ExitCodes.PluginUnavailable;
            result.Summary = inspection.Status == PluginInspectionStatus.UnsafePath
                ? "the plugin path is not safe or not supported (see the diagnostics)"
                : "the plugin could not be inspected (see the diagnostics)";
        }

        string? approveCommand = fingerprint is null ? null : CommandSupport.PowerShellCommand("approve", id.Value, ["--sha256", fingerprint.Fingerprint.Value, .. CommandSupport.LocationArguments(paths)]);
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", paths.Root.Value);
            writer.SafeString("pluginId", id.Value);
            writer.WriteString("inspectionStatus", inspection.Status.ToString());
            writer.SafeString("sha256", fingerprint?.Fingerprint.Value);
            writer.NullableNumber("contentBytes", fingerprint?.ContentLength);
            writer.SafeString("validatedFilePath", inspection.ValidatedFilePath);
            writer.WriteBoolean("approvalRequired", true);
            writer.WriteBoolean("approvalChecked", false);
            writer.WriteBoolean("discoveryComplete", discovery.IsComplete);
            writer.WriteBoolean("metadataFound", descriptor is not null);
            if (descriptor is null)
            {
                writer.WriteNull("metadata");
                writer.WriteNull("fileName");
                CommandSupport.WriteDiagnosticsJson(writer, "metadataDiagnostics", []);
            }
            else
            {
                writer.WritePropertyName("metadata");
                CommandSupport.WriteMetadataJson(writer, descriptor.Metadata);
                writer.WriteStartObject("fileName");
                CommandSupport.WriteFileNameJson(writer, descriptor.FileName);
                writer.WriteEndObject();
                CommandSupport.WriteDiagnosticsJson(writer, "metadataDiagnostics", descriptor.Diagnostics.Select(CliDiagnostic.From));
            }

            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin", id.Value);
            writer.Field("Plugin root", paths.Root.Value);
            writer.Value("Inspection", inspection.Status == PluginInspectionStatus.Inspected ? "succeeded" : $"FAILED ({inspection.Status})");
            if (fingerprint is not null)
            {
                writer.Field("Validated file", inspection.ValidatedFilePath);
                writer.Value("SHA-256", fingerprint.Fingerprint.Value);
                writer.Value("Size", CommandSupport.Bytes(fingerprint.ContentLength));
            }

            writer.Line();
            writer.Line("Metadata (untrusted text from the file's comments, read by discovery separately from the fingerprint; the file may have changed in between):");
            using (writer.Indent())
            {
                if (descriptor is null)
                {
                    writer.Line(discovery.IsComplete ? "(not found by discovery)" : "(not found by discovery; the scan was incomplete)");
                }
                else
                {
                    CommandSupport.WriteMetadataHuman(writer, descriptor.Metadata);
                    CommandSupport.WriteFileNameHuman(writer, descriptor.FileName);
                    writer.Diagnostics([.. descriptor.Diagnostics.Select(CliDiagnostic.From)], "Metadata diagnostics");
                }
            }

            writer.Line();
            writer.Line("Nothing was run or approved. Approval is required separately, and its current state was not checked here (use 'traywisp status').");
            if (fingerprint is not null)
            {
                writer.Line("Only the fingerprint identifies the content. Seeing it does not mean the script was reviewed or understood.");
                writer.Line("Next steps:");
                using (writer.Indent())
                {
                    writer.Line("1. Read the whole script file shown above.");
                    writer.Line("2. If you trust exactly this content, approve this fingerprint:");
                    using (writer.Indent())
                    {
                        writer.Line(approveCommand ?? $"traywisp approve --sha256 {fingerprint.Fingerprint.Value} -- 'PLUGIN_ID'   (a template: replace PLUGIN_ID with the plugin identity shown above, unescaped and with each ' doubled, and add any --root and --data-dir options before the --)");
                        writer.Line("If the file changes before you approve, the approval is refused.");
                    }

                    writer.Line("3. " + CommandSupport.ApprovalIsNotSandboxing);
                }
            }
        };
        return result;
    }

    public static CommandResult Approve(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null
            || !CommandSupport.CheckStorageOverlap(paths, result)
            || !CommandSupport.ValidateRoot(paths, result, cancellationToken)
            || !CommandSupport.CheckStorageDirectory(paths.TrustDirectory, "trust store", result, cancellationToken))
        {
            return result;
        }

        PluginTrustService? trust = CommandSupport.CreateTrustService(paths, environment, result);
        if (trust is null)
        {
            return result;
        }

        PluginId id = request.PluginId!;
        ContentFingerprint expected = request.Sha256!;
        PluginApprovalResult approval = trust.Approve(paths.Root.Value, id, expected, cancellationToken);
        result.AddRange(CliDiagnostic.From(approval.Diagnostics));
        if (TerminalText.HasNonAscii(id.Value))
        {
            result.Add(Lookalike());
        }

        (result.ExitCode, result.Summary) = approval.Status switch
        {
            PluginApprovalStatus.Approved or PluginApprovalStatus.AlreadyApproved => (ExitCodes.Success, null),
            PluginApprovalStatus.FingerprintMismatch => (ExitCodes.ApprovalDenied, "nothing was approved: the file's current content does not match the fingerprint you gave"),
            PluginApprovalStatus.StoreUnavailable => (ExitCodes.StorageFailed, "nothing was approved: the trust store is unavailable (see the diagnostics)"),
            _ => (ExitCodes.PluginUnavailable, "nothing was approved: the plugin is unavailable or its path is unsafe (see the diagnostics)"),
        };
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", paths.Root.Value);
            writer.SafeString("pluginId", id.Value);
            writer.WriteString("approvalStatus", approval.Status.ToString());
            writer.WriteBoolean("approved", approval.Succeeded);
            writer.WriteString("expectedSha256", expected.Value);
            writer.SafeString("currentSha256", approval.Current?.Fingerprint.Value);
            WriteApprovalJson(writer, "approval", approval.Approval);
            writer.WriteBoolean("approvalInvalidated", approval.ApprovalInvalidated);
            writer.WriteBoolean("pluginRun", false);
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin", id.Value);
            writer.Field("Plugin root", paths.Root.Value);
            writer.Value("Result", approval.Status switch
            {
                PluginApprovalStatus.Approved => "approved",
                PluginApprovalStatus.AlreadyApproved => "already approved for exactly this content (the existing approval was kept)",
                PluginApprovalStatus.FingerprintMismatch => "NOT approved: the current content differs from the fingerprint you gave",
                _ => $"NOT approved ({approval.Status})",
            });
            writer.Value("Expected SHA-256", expected.Value);
            if (approval.Current is not null)
            {
                writer.Value("Current SHA-256", approval.Current.Fingerprint.Value);
            }

            if (approval.Approval is PluginApproval approved)
            {
                writer.Value("Approval generation", approved.Generation.Value);
                writer.Value("Approved at (UTC)", approved.ApprovedAtUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture));
            }

            if (approval.ApprovalInvalidated)
            {
                writer.Line("An approval of earlier, different content was removed.");
            }

            if (approval.Status == PluginApprovalStatus.FingerprintMismatch)
            {
                writer.Line("The file changed after it was inspected, or the fingerprint was mistyped. Inspect it again and review the new content before approving.");
            }

            if (approval.Succeeded)
            {
                writer.Line(CommandSupport.ApprovalIsNotSandboxing);
                writer.Line("The plugin was not run. Use 'traywisp run' to run it once.");
            }
        };
        return result;
    }

    public static CommandResult Revoke(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();

        // Deliberately no root validation and no overlap check: approvals of plugins or roots that no longer exist, or of
        // misconfigured roots, must stay revocable.
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null || !CommandSupport.CheckStorageDirectory(paths.TrustDirectory, "trust store", result, cancellationToken))
        {
            return result;
        }

        PluginTrustService? trust = CommandSupport.CreateTrustService(paths, environment, result);
        if (trust is null)
        {
            return result;
        }

        PluginId id = request.PluginId!;
        PluginRevocationResult revocation = trust.Revoke(paths.Root.Value, id, cancellationToken);
        result.AddRange(CliDiagnostic.From(revocation.Diagnostics));
        (result.ExitCode, result.Summary) = revocation.Status switch
        {
            PluginRevocationStatus.Revoked or PluginRevocationStatus.NotApproved => (ExitCodes.Success, null),
            PluginRevocationStatus.StoreUnavailable => (ExitCodes.StorageFailed, "the revocation could not be completed: the trust store is unavailable or could not be updated (see the diagnostics)"),
            _ => (ExitCodes.PluginUnavailable, "the plugin root or identity is not supported (see the diagnostics)"),
        };
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", paths.Root.Value);
            writer.SafeString("pluginId", id.Value);
            writer.WriteString("revocationStatus", revocation.Status.ToString());
            writer.WriteBoolean("revoked", revocation.Status == PluginRevocationStatus.Revoked);
            WriteApprovalJson(writer, "revokedApproval", revocation.RevokedApproval);
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin", id.Value);
            writer.Field("Plugin root", paths.Root.Value);
            writer.Value("Result", revocation.Status switch
            {
                PluginRevocationStatus.Revoked => "approval revoked",
                PluginRevocationStatus.NotApproved => "no matching approval existed; nothing changed",
                PluginRevocationStatus.StoreUnavailable => "NOT revoked: the trust store is unavailable or could not be updated",
                _ => "NOT revoked: the plugin root or identity is not supported",
            });
            if (revocation.RevokedApproval is PluginApproval revoked)
            {
                writer.Value("Revoked generation", revoked.Generation.Value);
                writer.Value("Revoked SHA-256", revoked.Fingerprint.Value);
            }

            writer.Line("The plugin file was not changed or deleted.");
            if (revocation.Status == PluginRevocationStatus.Revoked)
            {
                writer.Line("A run already started by another TrayWisp process is not stopped by this; its next trust check denies it.");
            }
        };
        return result;
    }

    public static CommandResult Status(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null
            || !CommandSupport.CheckStorageOverlap(paths, result)
            || !CommandSupport.ValidateRoot(paths, result, cancellationToken)
            || !CommandSupport.CheckStorageDirectory(paths.TrustDirectory, "trust store", result, cancellationToken))
        {
            return result;
        }

        PluginTrustService? trust = CommandSupport.CreateTrustService(paths, environment, result);
        if (trust is null)
        {
            return result;
        }

        // One trust service for the whole command. Plugins are checked one at a time, in discovery order.
        PluginDiscoveryResult? discovery = null;
        var decisions = new List<(PluginId Id, PluginTrustDecision Decision)>();
        if (request.PluginId is PluginId single)
        {
            decisions.Add((single, trust.CheckTrust(paths.Root.Value, single, cancellationToken)));
        }
        else
        {
            discovery = PluginDiscovery.Discover(paths.Root.Value, PluginDiscoveryOptions.Default, cancellationToken);
            result.AddRange(discovery.Diagnostics.Select(CliDiagnostic.From));
            foreach (PluginDescriptor plugin in discovery.Plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decisions.Add((plugin.Id, trust.CheckTrust(paths.Root.Value, plugin.Id, cancellationToken)));
            }

            if (!discovery.IsComplete)
            {
                result.Add(IncompleteScan());
            }
        }

        if (decisions.Count == 1)
        {
            result.AddRange(CliDiagnostic.From(decisions[0].Decision.Diagnostics));
        }

        if (decisions.Any(entry => entry.Decision.Status == PluginTrustStatus.StoreUnavailable))
        {
            result.ExitCode = ExitCodes.StorageFailed;
            result.Summary = "the trust store is unavailable, so trust could not be determined (see the diagnostics)";
        }
        else if (discovery is { IsComplete: false })
        {
            result.ExitCode = ExitCodes.DiscoveryIncomplete;
            result.Summary = "the scan was incomplete; plugins missing from the list may still exist";
        }

        bool many = request.PluginId is null;
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", paths.Root.Value);
            if (discovery is null)
            {
                writer.WriteNull("discoveryComplete");
            }
            else
            {
                writer.WriteBoolean("discoveryComplete", discovery.IsComplete);
            }

            writer.WriteStartArray("plugins");
            foreach ((PluginId id, PluginTrustDecision decision) in decisions)
            {
                writer.WriteStartObject();
                writer.SafeString("pluginId", id.Value);
                writer.WriteString("trustStatus", decision.Status.ToString());
                writer.WriteBoolean("approved", decision.IsApproved);
                writer.SafeString("currentSha256", decision.Current?.Fingerprint.Value);
                writer.NullableNumber("contentBytes", decision.Current?.ContentLength);
                writer.WritePropertyName("approval");
                if (decision.Approval is PluginApprovalIdentity identity)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sha256", identity.Fingerprint.Value);
                    writer.WriteString("generation", identity.Generation.Value);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteBoolean("approvalInvalidated", decision.ApprovalInvalidated);
                writer.SafeString("validatedFilePath", decision.ValidatedFilePath);
                CommandSupport.WriteDiagnosticsJson(writer, "diagnostics", CliDiagnostic.From(decision.Diagnostics));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin root", paths.Root.Value);
            if (discovery is not null)
            {
                writer.Value("Scan", discovery.IsComplete
                    ? string.Create(CultureInfo.InvariantCulture, $"complete, {discovery.Plugins.Length} plugins")
                    : string.Create(CultureInfo.InvariantCulture, $"INCOMPLETE, {discovery.Plugins.Length} plugins checked; plugins missing from this list may still exist"));
            }

            writer.Line("Each status is an authoritative check made now (the file was hashed). It is not a lasting authorization, and a check that finds");
            writer.Line("changed content removes the obsolete approval from the trust store.");
            foreach ((PluginId id, PluginTrustDecision decision) in decisions)
            {
                writer.Line();
                writer.Field("Plugin", id.Value);
                using (writer.Indent())
                {
                    writer.Value("Trust status", decision.Status + " - " + Explain(decision.Status));
                    if (decision.Current is PluginFingerprint current)
                    {
                        writer.Value("Current SHA-256", current.Fingerprint.Value);
                    }

                    if (decision.Approval is PluginApprovalIdentity identity)
                    {
                        writer.Value("Approval generation", identity.Generation.Value);
                    }

                    if (decision.ApprovalInvalidated)
                    {
                        writer.Line("The obsolete approval was removed from the trust store by this check.");
                    }

                    if (many)
                    {
                        writer.Diagnostics([.. CliDiagnostic.From(decision.Diagnostics)]);
                    }
                }
            }
        };
        return result;
    }

    public static string Explain(PluginTrustStatus status) => status switch
    {
        PluginTrustStatus.Approved => "approved, and the current file matches the approval",
        PluginTrustStatus.PendingApproval => "not approved (never approved, revoked, or invalidated earlier)",
        PluginTrustStatus.ContentChanged => "the file differs from its approved content; the approval was invalidated and must be given again",
        PluginTrustStatus.StaleApproval => "the expected approval is no longer the current one",
        PluginTrustStatus.PluginUnavailable => "the file could not be checked now (missing, locked, unreadable or too large); its approval, if any, is kept",
        PluginTrustStatus.UnsafePath => "the path is unsafe or unsupported (link, alias, hidden, collision, overlap with the trust store)",
        PluginTrustStatus.StoreUnavailable => "the trust store could not be read or updated",
        _ => "unknown",
    };

    private static void WriteApprovalJson(Utf8JsonWriter writer, string name, PluginApproval? approval)
    {
        writer.WritePropertyName(name);
        if (approval is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("sha256", approval.Fingerprint.Value);
        writer.WriteString("generation", approval.Generation.Value);
        writer.Timestamp("approvedAtUtc", approval.ApprovedAtUtc);
        writer.WriteEndObject();
    }

    private static CliDiagnostic IncompleteScan() =>
        CliDiagnostic.Warning(CliDiagnosticCode.DiscoveryIncomplete, "The discovery scan was incomplete (see the discovery diagnostics). A plugin missing from it may still exist.");

    private static CliDiagnostic Lookalike() =>
        CliDiagnostic.Cli(
            PluginDiagnosticSeverity.Info,
            CliDiagnosticCode.LookalikeCharacters,
            "The plugin identity contains non-ASCII characters. TrayWisp does not detect lookalike characters; compare the identity carefully.");
}
