namespace Demo;

/// <summary>One line per thing that happened, from several services at once.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    // Colour only a terminal a person is reading. Redirected to a file or a CI log, escape codes are noise.
    private static readonly bool Colour =
        !Console.IsOutputRedirected && System.Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static void Line(string who, string glyph, string text, ConsoleColor colour)
    {
        lock (Gate)
        {
            Console.Write($"{DateTime.Now:HH:mm:ss} {who,-9} ");

            if (Colour)
            {
                Console.ForegroundColor = colour;
            }

            Console.Write($"{glyph} {text}");

            if (Colour)
            {
                Console.ResetColor();
            }

            Console.WriteLine();
        }
    }

    public static void Ok(string who, string glyph, string text) => Line(who, glyph, text, ConsoleColor.Green);

    public static void Info(string who, string glyph, string text) => Line(who, glyph, text, ConsoleColor.Cyan);

    public static void Warn(string who, string glyph, string text) => Line(who, glyph, text, ConsoleColor.Yellow);

    public static void Bad(string who, string glyph, string text) => Line(who, glyph, text, ConsoleColor.Red);

    public static void Plain(string text = "")
    {
        lock (Gate)
        {
            Console.WriteLine(text);
        }
    }
}
