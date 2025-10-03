using System;
using System.Collections.Generic;
using System.Linq;
using StackValueTracer;

namespace SampleLibrary;

[TraceValues]
public class StackDriver
{
    public void Trigger(int number)
    {
        StepOne(number, new[] { 1, 2, 3, 4 });
    }

    private void StepOne(int value, int[] items)
    {
        StepTwo(value * 2, items);
    }

    private void StepTwo(int value, IReadOnlyList<int> items)
    {
        StepThree(value.ToString(), items.Select(i => i * 10).ToList());
    }

    private void StepThree(string text, IReadOnlyList<int> transformed)
    {
        throw new InvalidOperationException($"Library boom: {text} / {string.Join(',', transformed)}");
    }
}
