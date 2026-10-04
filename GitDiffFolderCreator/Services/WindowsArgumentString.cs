using System.Text;

namespace GitDiffFolderCreator.Services;

/// <summary>
/// Builds a Windows command line from an argument vector, following the quoting rules the C
/// runtime and <c>CommandLineToArgvW</c> use to split it back apart.
/// </summary>
/// <remarks>
/// .NET Framework's <c>ProcessStartInfo</c> only accepts a pre-formatted command line, so this is
/// what keeps a repository-controlled pathname from turning into shell syntax. The result is still
/// passed to git directly - there is no shell in the path.
/// </remarks>
internal static class WindowsArgumentString
{
    private static readonly char[] NeedsQuoting = { ' ', '\t', '"' };

    public static string Build(IEnumerable<string> arguments)
    {
        StringBuilder builder = new StringBuilder();

        foreach (string argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            Append(builder, argument ?? string.Empty);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(NeedsQuoting) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        int index = 0;
        while (index < argument.Length)
        {
            int backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                // Backslashes that precede the closing quote must be doubled, otherwise they escape it.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(argument[index]);
            }

            index++;
        }

        builder.Append('"');
    }
}