using MemShack.Core.Constants;
using MemShack.Core.Utilities;

namespace MemShack.Application.Layers;

public sealed class Layer0
{
    private string? _text;

    public Layer0(string? identityPath = null)
    {
        IdentityPath = identityPath
            ?? Path.Combine(
                MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory()),
                ConfigFileNames.IdentityText);
    }

    public string IdentityPath { get; }

    public string Render()
    {
        if (_text is not null)
        {
            return _text;
        }

        _text = File.Exists(IdentityPath)
            ? File.ReadAllText(IdentityPath).Trim()
            : "## L0 - IDENTITY\nNo identity configured. Create ~/.mempalace/identity.txt";

        return _text;
    }

    public int TokenEstimate() => Render().Length / 4;
}
