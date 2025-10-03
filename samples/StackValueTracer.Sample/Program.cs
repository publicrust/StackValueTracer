using System.Globalization;
using SampleLibrary;
using StackValueTracer;

TraceBootstrap.Enable(typeof(Program).Assembly, typeof(StackDriver).Assembly);

try
{
    await DemoWorkflow.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Exception: {ex.Message}");
    Console.WriteLine("Value stack:");
    Console.WriteLine(ValueStack.Dump(clear: true));
}

[TraceValues]
internal static class DemoWorkflow
{
    public static async Task RunAsync()
    {
        await StepOneAsync(42, "hello", new[] { 1, 2, 3, 4, 5, 6 });
    }

    private static async Task StepOneAsync(int seed, string message, IReadOnlyList<int> numbers)
    {
        await StepTwoAsync(seed - 2, message.ToUpperInvariant(), numbers);
    }

    private static async Task StepTwoAsync(int value, string message, IReadOnlyList<int> numbers)
    {
        await Task.Delay(10);
        StepThree(value.ToString(CultureInfo.InvariantCulture), numbers);
    }

    private static void StepThree(string text, IEnumerable<int> numbers)
    {
        var driver = new StackDriver();
        var parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
            ? intValue
            : 0;
        driver.Trigger(parsed);
    }
}
