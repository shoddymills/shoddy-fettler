using System.Text;

namespace Burler;

/// <summary>
/// burler: the disclosure screen's model host.
///
/// <para>The trade's own word for it. A burler picks the faults out of
/// finished cloth before it leaves the mill, which is this job exactly -
/// inspect what is about to go out, and stop it if something is in it.
/// <c>percher</c>, the other cloth inspector, was the better word and is
/// taken: <c>Shoddy.Perch</c> already means something else here.</para>
///
/// <para><b>It speaks only when spoken to, and it always answers.</b> A
/// request that cannot be served is answered with a refusal rather than
/// with silence, because silence on this pipe reads to Fettler as a hung
/// process - which it would then kill and report as a timeout, sending
/// somebody to look at the wrong thing entirely.</para>
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        string? models = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--models") models = args[i + 1];

        if (models is null)
        {
            Console.Error.WriteLine("burler needs --models DIRECTORY, naming where the screening models are.");
            return 2;
        }

        // EXPLICIT UTF-8 on both pipes, not Console.In and Console.Out.
        // Console.In decodes with the console's input code page - 437 or
        // 850 on a default Windows install - so the three bytes of an em
        // dash arrive as three separate characters. fettle shipped that
        // bug once: the round trip was self-consistent, so hashes matched
        // and nothing downstream could tell. Here it would be worse than
        // corruption, because a mangled payload is one whose entities
        // quietly stop matching, and the screen would report a clean
        // verdict on text it never really saw.
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        // Built on the first request rather than here, so a burler
        // started with a directory it cannot use still answers the
        // request that revealed it - with the reason.
        Models? loaded = null;

        try
        {
            while (input.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                output.WriteLine(Serve(line, models, ref loaded));
            }
        }
        finally
        {
            loaded?.Dispose();
        }

        return 0;
    }

    static string Serve(string line, string models, ref Models? loaded)
    {
        Request? request = Protocol.Parse(line, out string error);
        if (request is null) return Protocol.Failed(error);

        if (request.Op != "screen")
            return Protocol.Failed($"'{request.Op}' is not an operation this knows");

        try
        {
            loaded ??= new Models(models);

            var findings = new List<Finding>();

            foreach (string category in request.Categories)
            {
                IReadOnlyList<Span> spans = loaded.Screen(category, request.Payload);
                if (spans.Count == 0) continue;

                var ranges = new List<(int Start, int End)>(spans.Count);
                foreach (Span one in spans) ranges.Add((one.Start, one.End));

                findings.Add(new Finding(category, ranges));
            }

            return Protocol.Found(findings);
        }
        catch (Refusal r)
        {
            return Protocol.Failed(r.Message);
        }
        catch (Exception e)
        {
            // The catch-all is the fail-closed rule made structural. Any
            // exception at all becomes a refusal that the other side turns
            // into a denied disclosure, so there is no fault anywhere in
            // this program whose consequence is a payload being served
            // unscreened.
            return Protocol.Failed($"the screen failed unexpectedly: {e.GetType().Name}: {e.Message}");
        }
    }
}
