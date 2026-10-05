using System.Text;
using SharpFuzz;
using SpankyhoOptimizer.Services;

static void Exercise(string input)
{
    try
    {
        _ = WorkerPayloads.Decode<WorkerAck>(input);
    }
    catch (FormatException) { }
    catch (InvalidOperationException) { }
    catch (System.Text.Json.JsonException) { }
}

if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
{
    var seed = new Random(1337);
    string[] corpus =
    [
        "",
        " ",
        "e30=",
        Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")),
        Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Ok\":true}")),
        "!!!!",
        "AA=="
    ];

    foreach (string item in corpus)
        Exercise(item);

    for (int i = 0; i < 5000; i++)
    {
        int len = seed.Next(0, 512);
        byte[] bytes = new byte[len];
        seed.NextBytes(bytes);
        string candidate = i % 2 == 0
            ? Convert.ToBase64String(bytes)
            : Encoding.Latin1.GetString(bytes);
        Exercise(candidate);
    }

    Console.WriteLine("FUZZ_SMOKE_OK cases=5007");
    return;
}

Fuzzer.OutOfProcess.Run(Exercise);
