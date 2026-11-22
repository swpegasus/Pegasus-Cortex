var failures = new List<string>();
foreach (var test in LearningFeedbackApplicationTests.All)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT {LearningFeedbackApplicationTests.All.Count - failures.Count}/{LearningFeedbackApplicationTests.All.Count} passed");
return failures.Count == 0 ? 0 : 1;
