using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<ICalculator, Calculator>();
builder.Services.AddSingleton<ICalculatorStack, CalculatorStack>();
builder.Services.AddSingleton<ICalculationHistory, CalculationHistory>();

var app = builder.Build();

app.MapGet("/calculator/health", () => Results.Text("OK"));

app.MapPost("/calculator/independent/calculate", (CalculationRequest? request, ICalculator calculator,
            ICalculationHistory history, CancellationToken cancellationToken) =>
{
    if (request is null ||
        request.Operation is null ||
        !Enum.TryParse<Operation>(request.Operation, true, out var operation))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: unknown operation: {request?.Operation}"));
    }

    if (request.Arguments is null)
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: Not enough arguments to perform the operation {Enum.GetName(operation) ?? throw new InvalidOperationException()}"));
    }

    if (!calculator.TryCalculate(operation, request.Arguments, out var result, out var error))
    {
        Debug.Assert(error is not null);
        return Results.Conflict(CalculationResultBuilder.FromError(error.Value, Enum.GetName(operation) ?? throw new InvalidOperationException()));
    }

    Debug.Assert(result is not null);
    history.Add(Flavor.INDEPENDENT, operation, request.Arguments, result.Value);
    return Results.Ok(CalculationResultBuilder.FromResult(result.Value));
});

app.MapGet("/calculator/stack/size", (ICalculatorStack stack) => Results.Ok(CalculationResultBuilder.FromResult(stack.StackCalculatorSize)));
app.MapPut("/calculator/stack/arguments", (CalculationStackPutRequest? request, ICalculatorStack stack) =>
{
    if (request is null || request.Arguments is null)
    {
        return Results.BadRequest(CalculationResultBuilder.FromErrorMessage("A list of arguments is required in request body"));
    }
    stack.PushArgumentsToStackCalculator(request.Arguments);
    return Results.Ok(CalculationResultBuilder.FromResult(stack.StackCalculatorSize));
});
app.MapDelete("/calculator/stack/arguments", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "count")] int count, ICalculatorStack stack) =>
{
    var stackCount = stack.StackCalculatorSize;
    if (!stack.TryPopStackCalculatorArguments(count, out var arguments))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: cannot remove {count} from the stack. It has only {stackCount} arguments"));
    }

    return Results.Ok(CalculationResultBuilder.FromResult(stack.StackCalculatorSize));
});
app.MapPut("/calculator/stack/operate", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "operation")] string? operationQuery,
            ICalculatorStack stack, ICalculator calculator, ICalculationHistory history) =>
{
    if (operationQuery is null || !Enum.TryParse<Operation>(operationQuery, true, out var operation))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: unknown operation: {operationQuery}"));
    }

    var stackCount = stack.StackCalculatorSize;
    var requiredAmount = calculator.GetRequriedArgumentsCount(operation);
    if (!stack.TryPopStackCalculatorArguments(requiredAmount, out var arguments))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: cannot implement operation {Enum.GetName(operation) ?? throw new InvalidOperationException()}. It requires {requiredAmount} arguments and the stack has only {stackCount} arguments"));
    }

    Debug.Assert(arguments is not null);
    if (!calculator.TryCalculate(operation, arguments, out var result, out var error))
    {
        Debug.Assert(error is not null);
        return Results.Conflict(CalculationResultBuilder.FromError(error.Value, operationQuery));
    }

    Debug.Assert(result is not null);
    history.Add(Flavor.STACK, operation, arguments, result.Value);
    return Results.Ok(CalculationResultBuilder.FromResult(result.Value));
});

app.MapGet("/calculator/history", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "flavor")] string? flavorString, ICalculationHistory history) =>
{
    Flavor? flavor = flavorString switch
    {
        "STACK" => Flavor.STACK,
        "INDEPENDENT" => Flavor.INDEPENDENT,
        _ => null
    };

    return Results.Ok(CalculationResultBuilder.FromResult(history.CalculationsByFlavor(flavor)));
});

app.Run();

enum Flavor
{
    STACK,
    INDEPENDENT
}
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
enum CalculationError
{
    NotEnoughArguments,
    TooManyArguments,
    DivideByZero,
    NegativeFactorialNotSupported
}
record class CalculationRequest(int[]? Arguments, string? Operation);
record class CalculatorResult(int? Result, string? ErrorMessage);
record class CalculationStackPutRequest(int[]? Arguments);
record class Calculation(string Flavor, string Operation, int[] Arguments, int Result);
record class CalculatorHistoryResult(Calculation[] Result);
static class CalculationResultBuilder
{
    const string neaErr = "Error: Not enough arguments to perform the operation {0}";
    const string tmaErr = "Error: Too many arguments to perform the operation {0}";
    const string dvByZeroErr = "Error while performing operation Divide: division by 0";
    const string facLessZeroErr = "Error while performing operation Factorial: not supported for the negative number";

    public static CalculatorResult FromResult(int result) => new CalculatorResult(result, null);
    public static CalculatorResult FromErrorMessage(string errorMessage) => new CalculatorResult(null, errorMessage);
    public static CalculatorResult FromError(CalculationError error, string operation) => error switch
    {
        CalculationError.NotEnoughArguments => FromErrorMessage(string.Format(neaErr, operation)),
        CalculationError.TooManyArguments => FromErrorMessage(string.Format(tmaErr, operation)),
        CalculationError.DivideByZero => FromErrorMessage(dvByZeroErr),
        CalculationError.NegativeFactorialNotSupported => FromErrorMessage(facLessZeroErr),
        _ => throw new InvalidOperationException(),
    };
    public static CalculatorHistoryResult FromResult(Calculation[] result) => new CalculatorHistoryResult(result);
}

interface ICalculatorStack
{
    public int StackCalculatorSize { get; }
    public void PushArgumentsToStackCalculator(int[] arguments);
    public bool TryPopStackCalculatorArguments(int amount, out int[]? container);
}
interface ICalculationHistory
{
    void Add(Flavor flavor, Operation operation, int[] arguments, int result);
    Calculation[] CalculationsByFlavor(Flavor? flavor);
}
interface ICalculator
{
    bool TryCalculate(Operation operation, int[] arguments, out int? result, out CalculationError? error);
    int GetRequriedArgumentsCount(Operation operation);
}

class CalculatorStack : ICalculatorStack
{
    private readonly ConcurrentStack<int> argStack = new();

    public int StackCalculatorSize => argStack.Count;

    public void PushArgumentsToStackCalculator(int[] arguments) => argStack.PushRange(arguments);

    public bool TryPopStackCalculatorArguments(int amount, out int[]? container)
    {
        container = null;
        if (amount < 0) return false;

        container = new int[amount];
        if (amount == 0) return true;

        var popedAmount = argStack.TryPopRange(container, 0, amount);

        if (amount != popedAmount)
        {
            for (var i = popedAmount - 1; i >= 0; i--)
            {
                argStack.Push(container[i]);
            }

            container = null;
            return false;
        }

        return true;
    }
}
class CalculationHistory : ICalculationHistory
{
    private readonly ConcurrentQueue<Calculation> stackHistory = new();
    private readonly ConcurrentQueue<Calculation> independentHistory = new();
    public void Add(Flavor flavor, Operation operation, int[] arguments, int result)
    {
        var calculation = new Calculation(Enum.GetName<Flavor>(flavor)!, Enum.GetName<Operation>(operation)!, arguments, result);
        switch (flavor)
        {
            case Flavor.STACK:
                stackHistory.Enqueue(calculation);
                break;
            case Flavor.INDEPENDENT:
                independentHistory.Enqueue(calculation);
                break;
            default:
                throw new InvalidOperationException($"Cannot add calculation with flavor: {flavor}, did you forgot to add support for it?");
        }
    }
    public Calculation[] CalculationsByFlavor(Flavor? flavor)
    {
        return flavor switch
        {
            Flavor.STACK => stackHistory.ToArray(),
            Flavor.INDEPENDENT => independentHistory.ToArray(),
            null => stackHistory.Concat(independentHistory).ToArray(),
            _ => throw new InvalidOperationException($"Cannot return calculations by flavor: {flavor}, did you forgot to add support for it?")
        };
    }
}
class Calculator : ICalculator
{
    public int GetRequriedArgumentsCount(Operation operation)
    {
        return operation switch
        {
            Operation.Plus or Operation.Minus or Operation.Times or Operation.Divide or Operation.Pow => 2,
            Operation.Abs or Operation.Fact => 1,
            _ => throw new InvalidOperationException($"Operation: {Enum.GetName(operation)} not supported, Did you forgot to add this in {nameof(TryCalculate)}?")
        };

    }

    public bool TryCalculate(Operation operation, int[] arguments, out int? result, out CalculationError? error)
    {
        result = null;
        error = null;

        return (operation, arguments) switch
        {
            (_, { Length: < 1 }) => AssaingErrorAndRetureFalse(CalculationError.NotEnoughArguments, out error),
            (_, { Length: > 2 }) => AssaingErrorAndRetureFalse(CalculationError.TooManyArguments, out error),
            (Operation.Abs or Operation.Fact, { Length: > 1 }) => AssaingErrorAndRetureFalse(CalculationError.TooManyArguments, out error),
            (Operation.Plus or Operation.Minus or Operation.Times or Operation.Divide or Operation.Pow, { Length: < 2 }) => AssaingErrorAndRetureFalse(CalculationError.NotEnoughArguments, out error),
            (Operation.Divide, [_, 0]) => AssaingErrorAndRetureFalse(CalculationError.DivideByZero, out error),
            (Operation.Fact, [< 0]) => AssaingErrorAndRetureFalse(CalculationError.NegativeFactorialNotSupported, out error),

            (Operation.Plus, _) => AssaingResAndRetureTrue(arguments[0] + arguments[1], out result),
            (Operation.Minus, _) => AssaingResAndRetureTrue(arguments[0] - arguments[1], out result),
            (Operation.Times, _) => AssaingResAndRetureTrue(arguments[0] * arguments[1], out result),
            (Operation.Divide, _) => AssaingResAndRetureTrue(arguments[0] / arguments[1], out result),
            (Operation.Pow, _) => AssaingResAndRetureTrue((int)Math.Pow(arguments[0], arguments[1]), out result),
            (Operation.Abs, _) => AssaingResAndRetureTrue(Math.Abs(arguments[0]), out result),
            (Operation.Fact, _) => AssaingResAndRetureTrue(Factorial(arguments[0]), out result),
            _ => throw new InvalidOperationException($"Operation: {Enum.GetName(operation)} not supported, Did you forgot to add this in {nameof(TryCalculate)}?")
        };

        static bool AssaingErrorAndRetureFalse(CalculationError value, out CalculationError? error)
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
