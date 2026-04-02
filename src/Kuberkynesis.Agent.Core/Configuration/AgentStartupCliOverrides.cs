namespace Kuberkynesis.Agent.Core.Configuration;

public sealed record AgentStartupCliOverrides(
    int? Port,
    IReadOnlyList<string> AdditionalOrigins,
    string? KubeConfigPath,
    bool DisableBrowserOpen,
    bool EnableDiagnostics)
{
    public static AgentStartupCliOverrides Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int? port = null;
        var origins = new List<string>();
        string? kubeConfigPath = null;
        var disableBrowserOpen = false;
        var enableDiagnostics = false;

        for (var index = 0; index < args.Count; index++)
        {
            var current = args[index];

            if (index is 0 && string.Equals(current, "start", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadValue(current, "--port", args, ref index, out var portValue))
            {
                if (!int.TryParse(portValue, out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    throw new ArgumentException($"The value '{portValue}' is not a valid TCP port for --port.", nameof(args));
                }

                port = parsedPort;
                continue;
            }

            if (TryReadValue(current, "--origin", args, ref index, out var originValue))
            {
                if (string.IsNullOrWhiteSpace(originValue))
                {
                    throw new ArgumentException("The --origin flag requires a non-empty absolute origin.", nameof(args));
                }

                origins.Add(originValue.Trim());
                continue;
            }

            if (TryReadValue(current, "--kubeconfig", args, ref index, out var kubeConfigValue))
            {
                if (string.IsNullOrWhiteSpace(kubeConfigValue))
                {
                    throw new ArgumentException("The --kubeconfig flag requires a non-empty file path.", nameof(args));
                }

                kubeConfigPath = kubeConfigValue.Trim();
                continue;
            }

            if (string.Equals(current, "--no-browser-open", StringComparison.OrdinalIgnoreCase))
            {
                disableBrowserOpen = true;
                continue;
            }

            if (string.Equals(current, "--diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                enableDiagnostics = true;
                continue;
            }

            throw new ArgumentException(
                $"The startup argument '{current}' is not supported. Use 'start' followed by supported flags such as --port, --origin, --kubeconfig, --no-browser-open, or --diagnostics.",
                nameof(args));
        }

        return new AgentStartupCliOverrides(
            Port: port,
            AdditionalOrigins: origins
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            KubeConfigPath: kubeConfigPath,
            DisableBrowserOpen: disableBrowserOpen,
            EnableDiagnostics: enableDiagnostics);
    }

    public void ApplyTo(AgentRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Port is not null)
        {
            options.PublicUrl = RewriteLoopbackPort(options.PublicUrl, Port.Value);
        }

        foreach (var origin in AdditionalOrigins)
        {
            if (!options.Origins.Interactive.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                options.Origins.Interactive.Add(origin);
            }
        }

        if (DisableBrowserOpen)
        {
            options.UiLaunch.AutoOpenBrowser = false;
        }
    }

    private static bool TryReadValue(string current, string flag, IReadOnlyList<string> args, ref int index, out string? value)
    {
        if (current.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = current[(flag.Length + 1)..];
            return true;
        }

        if (!string.Equals(current, flag, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"The {flag} flag requires a value.", nameof(args));
        }

        value = args[++index];
        return true;
    }

    private static string RewriteLoopbackPort(string configuredUrl, int port)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configuredUri))
        {
            return $"http://127.0.0.1:{port}";
        }

        var builder = new UriBuilder(configuredUri)
        {
            Port = port
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
}
