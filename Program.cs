using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument(config =>
        {
            config.DocumentName = "CalculatorAPI";
            config.Title = "CalculatorAPI v1";
            config.Version = "v1";
        });

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<ICalculator, Calculator>();
builder.Services.AddSingleton<ICalculatorStack, CalculatorStack>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseOpenApi();
    app.UseSwaggerUi(config =>
            {
                config.DocumentTitle = "CalculatorAPI";
                config.Path = "/swagger";
                config.DocumentPath = "swagger/{documentName}/swagger.json";
                config.DocExpansion = "full";
            });
}

app.MapGet("/calculator/health", () => Results.Text("OK"));
app.MapPost("/calculator/independent/calculate", (CalculationRequest? request, ICalculator calculator, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (request is null ||
        request.Arguments is null ||
        request.Operation is null ||
        !Enum.TryParse<Operation>(request.Operation, true, out var operation))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: unknown operation: {request?.Operation}"));
    }

    if (!calculator.TryCalculate(operation, request.Arguments, out var result, out var errorMessage))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage(errorMessage!));
    }

    return Results.Ok(CalculationResultBuilder.FromResult(result!.Value));
});

app.Run();

enum Operation
{
    Plus,
    Minus,
    Times,
    Divide,
    Pow,
    Abs,
    Fact
}

interface ICalculatorStack
{
    public int StackCalculatorSize { get; }
    public void PushToStackCalculator(int argument);
    public bool TryPopStackCalculatorArguments(int amount, out int[] container);
}

class CalculatorStack : ICalculatorStack
{
    private readonly ConcurrentStack<int> argStack = new();

    public int StackCalculatorSize => argStack.Count;

    public void PushToStackCalculator(int argument) => argStack.Push(argument);

    public bool TryPopStackCalculatorArguments(int amount, out int[] container)
    {
        if (amount < 1) throw new ArgumentOutOfRangeException(nameof(amount), amount, "Cannot be less then 1");
        if (amount > 2) throw new ArgumentOutOfRangeException(nameof(amount), amount, "Cannot be more then 2");

        container = new int[amount];
        var popedAmount = argStack.TryPopRange(container, 0, amount);

        if (amount != popedAmount)
        {
            if (popedAmount == 1)
            {
                argStack.Push(container[0]);
            }

            return false;
        }

        if (amount == 2)
        {
            var temp = container[0];
            container[0] = container[1];
            container[1] = temp;
        }

        return true;
    }
}

interface ICalculatorStorage
{

}

interface ICalculator
{
    bool TryCalculate(Operation operation, List<int> arguments, out int? result, out string? errorMessage);
}

class Calculator : ICalculator
{
    public bool TryCalculate(Operation operation, List<int> arguments, out int? result, out string? errorMessage)
    {
        const string neaErr = "Error: Not enough arguments to perform the operation {0}";
        const string tmaErr = "Error: Too many arguments to perform the operation {0}";
        const string dvByZeroErr = "Error while performing operation Divide: division by 0";
        const string facLessZeroErr = "Error while performing operation Factorial: not supported for the negative number";
        result = null;
        errorMessage = null;

        return (operation, count: arguments.Count) switch
        {
            (_, > 2) => AssaingErrorAndRetureFalse(string.Format(tmaErr, Enum.GetName(operation)), out errorMessage),
            (_, < 1) => AssaingErrorAndRetureFalse(string.Format(neaErr, Enum.GetName(operation)), out errorMessage),
            (Operation.Abs or Operation.Fact, > 1) => AssaingErrorAndRetureFalse(string.Format(tmaErr, Enum.GetName(operation)), out errorMessage),

            (Operation.Plus, _) => AssaingResAndRetureTrue(arguments[0] + arguments[1], out result),
            (Operation.Minus, _) => AssaingResAndRetureTrue(arguments[0] - arguments[1], out result),
            (Operation.Times, _) => AssaingResAndRetureTrue(arguments[0] * arguments[1], out result),
            (Operation.Divide, _) => arguments[1] != 0 ? AssaingResAndRetureTrue(arguments[0] / arguments[1], out result) : AssaingErrorAndRetureFalse(dvByZeroErr, out errorMessage),
            (Operation.Pow, _) => AssaingResAndRetureTrue((int)Math.Pow(arguments[0], arguments[1]), out result),
            (Operation.Abs, _) => AssaingResAndRetureTrue(Math.Abs(arguments[0]), out result),
            (Operation.Fact, _) => arguments[0] >= 0 ? AssaingResAndRetureTrue(Factorial(arguments[0]), out result) : AssaingErrorAndRetureFalse(facLessZeroErr, out errorMessage),
            _ => throw new InvalidOperationException($"Operation: {Enum.GetName(operation)} not supported, Did you forgot to add this in {nameof(TryCalculate)}?")
        };

        static bool AssaingErrorAndRetureFalse(string value, out string? error)
        {
            error = value;
            return false;
        }

        static bool AssaingResAndRetureTrue(int value, out int? res)
        {
            res = value;
            return true;
        }
    }

    private static int Factorial(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));

        var f = 1;
        for (var i = 2; i <= n; i++)
            f *= i;
        return f;
    }
}
record class CalculationRequest(List<int>? Arguments, string? Operation);
record class CalculationResult(int? Result, string? ErrorMessage);
static class CalculationResultBuilder
{
    public static CalculationResult FromResult(int result) => new CalculationResult(result, null);
    public static CalculationResult FromErrorMessage(string errorMessage) => new CalculationResult(null, errorMessage);
}

