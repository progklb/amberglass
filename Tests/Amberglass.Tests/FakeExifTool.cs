using System.Runtime.CompilerServices;

namespace Amberglass.Tests;

/// <summary>
/// A shell script that speaks just enough of ExifTool's <c>-stay_open</c> protocol to exercise
/// <see cref="Amberglass.Metadata.ExifTool.ExifToolSession"/> without ExifTool installed:
/// it reads argument lines from stdin, and on <c>-execute{n}</c> emits a canned response followed
/// by <c>{ready{n}}</c>.
///
/// This verifies the process plumbing — argument framing, response matching, session reuse. It does
/// not validate real tag output, which needs the real tool.
/// </summary>
internal sealed class FakeExifTool : IDisposable
{
    private readonly TempFolder _folder = new();

    /// <param name="responseBody">Written verbatim before the ready marker for every request.</param>
    /// <param name="countInvocationsTo">Optional file that gets one line appended per process start.</param>
    /// <param name="terminateBodyWithNewline">
    /// Real ExifTool ends its output with a newline, so the ready marker gets its own line. Setting
    /// this false reproduces the marker sharing a line with the payload.
    /// </param>
    /// <param name="answerDelay">How long to wait before answering, so a caller can give up mid-request.</param>
    public FakeExifTool(string responseBody, string? countInvocationsTo = null, bool terminateBodyWithNewline = true, double answerDelay = 0)
    {
        Directory = _folder.Path;
        var scriptPath = Path.Combine(Directory, "exiftool");

        var responsePath = Path.Combine(Directory, "response.txt");
        if (terminateBodyWithNewline && !responseBody.EndsWith('\n'))
        {
            responseBody += "\n";
        }

        File.WriteAllText(responsePath, responseBody);

        var startupCounter = countInvocationsTo is null
            ? string.Empty
            : $"echo started >> {Shell(countInvocationsTo)}\n";

        // Reads arg lines; on -execute<n> prints the canned body then {ready<n>}. Written without
        // C# interpolation because the shell's own ${...} expansions collide with it.
        const string template = """
            #!/bin/sh
            __STARTUP__while IFS= read -r line; do
              case "$line" in
                -execute*)
                  __DELAY__cat __RESPONSE__
                  echo "{ready${line#-execute}}"
                  ;;
                False)
                  exit 0
                  ;;
              esac
            done

            """;

        var script = template
            .Replace("__STARTUP__", startupCounter)
            .Replace("__RESPONSE__", Shell(responsePath))
            .Replace("__DELAY__", answerDelay > 0
                ? $"sleep {answerDelay.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n                  "
                : string.Empty);

        File.WriteAllText(scriptPath, script.ReplaceLineEndings("\n"));
        MakeExecutable(scriptPath);
        Path_ = scriptPath;
    }

    /// <summary>Directory to hand to a locator via its override environment variable.</summary>
    public string Directory { get; }

    public string Path_ { get; }

    private static string Shell(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Skips a test on Windows, where the /bin/sh stub cannot run.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    public void Dispose() => _folder.Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string SampleJson(string sourceFile) => $$"""
        [{
          "SourceFile": "{{sourceFile.Replace("\\", "\\\\")}}",
          "EXIF:Make": "Canon",
          "EXIF:Model": "Canon EOS R5",
          "EXIF:LensModel": "RF100-500mm F4.5-7.1 L IS USM",
          "EXIF:ISO": 800,
          "Composite:ShutterSpeed": "1/1250",
          "Composite:Aperture": 7.1,
          "EXIF:FocalLength": "500.0 mm",
          "EXIF:DateTimeOriginal": "2024:06:01 09:15:22",
          "Composite:GPSPosition": "51 deg 30' N, 0 deg 7' W",
          "EXIF:Orientation": "Horizontal (normal)",
          "XMP:Title": "Lioness at dawn",
          "XMP:Description": "Early light on the plain",
          "XMP:Subject": ["wildlife", "Namibia", "lioness"],
          "XMP:Rating": 4,
          "XMP:Label": "Green",
          "XMP:Creator": "Kevin Baynham",
          "XMP:Rights": "© 2024 Kevin Baynham",
          "XMP:Headline": "Dawn patrol"
        }]
        """;
}
