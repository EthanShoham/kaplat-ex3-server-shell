using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Serilog;
using Serilog.Configuration;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Settings.Configuration;

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
builder.Services.AddSingleton<IRequestCounter, RequestCounter>();
builder.Services.AddSingleton<IDictionary<string, LoggingLevelSwitch>>((_) => new Dictionary<string, LoggingLevelSwitch>());
builder.Services.AddSingleton<IDictionary<string, LogEventLevel>>((_) => new Dictionary<string, LogEventLevel>
{
    { "DEBUG", LogEventLevel.Debug },
    { "INFO", LogEventLevel.Information },
    { "ERROR", LogEventLevel.Error }
});
builder.Services.AddSingleton<IDictionary<LogEventLevel, string>>((_) => new Dictionary<LogEventLevel, string>
{
    { LogEventLevel.Verbose, "TRACE" },
    { LogEventLevel.Debug, "DEBUG" },
    { LogEventLevel.Information, "INFO" },
    { LogEventLevel.Warning, "WARN" },
    { LogEventLevel.Error, "ERROR" },
    { LogEventLevel.Fatal, "CRITICAL" }
});
builder.Services.AddSerilog((sp, configuration) =>
{
    var switchesDict = sp.GetRequiredService<IDictionary<string, LoggingLevelSwitch>>();
    var readerOptions = new ConfigurationReaderOptions
    {
        OnLevelSwitchCreated = (name, switchLogger) => switchesDict[name.Substring(1).ToKababCase()] = switchLogger
    };
    configuration.ReadFrom.Configuration(builder.Configuration, readerOptions);
});

var app = builder.Build();

app.UseMiddleware<RequestNumberMiddleware>();

app.UseMiddleware<RequestLoggerMiddleware>();

app.MapGet("/calculator/health", () => Results.Text("OK"));

app.MapPost("/calculator/independent/calculate", (CalculationRequest? request, ICalculator calculator,
            ICalculationHistory history, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (request is null ||
        request.Operation is null ||
        !Enum.TryParse<Operation>(request.Operation, true, out var operation))
    {
        var err = CalculationResultBuilder.FromErrorMessage($"Error: unknown operation: {request?.Operation}");
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }

    if (request.Arguments is null)
    {
        var err = CalculationResultBuilder.FromErrorMessage($"Error: Not enough arguments to perform the operation {request.Operation}");
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }

    if (!calculator.TryCalculate(operation, request.Arguments, out var result, out var error))
    {
        Debug.Assert(error is not null);
        var err = CalculationResultBuilder.FromError(error.Value, request.Operation);
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }

    Debug.Assert(result is not null);
    logger.LogInformation("Performing operation {opration}. Result is {result}", request.Operation, result);
    logger.LogDebug("Performing operation: {operation}({arguments}) = {result}", request.Operation, string.Join(',', request.Arguments), result);
    history.Add(Flavor.Independent, request.Operation, request.Arguments, result.Value);
    return Results.Ok(CalculationResultBuilder.FromResult(result.Value));
});

app.MapGet("/calculator/stack/size", (ICalculatorStack stack, ILogger<Program> logger) =>
{
    var stackSize = stack.StackCalculatorSize;
    logger.LogInformation("Stack size is {stackSize}", stackSize);
    logger.LogDebug("Stack content (first == top): [{stackContent}]", string.Join(", ", stack.Content));
    return Results.Ok(CalculationResultBuilder.FromResult(stackSize));
});
app.MapPut("/calculator/stack/arguments", (CalculationStackPutRequest? request, ICalculatorStack stack, ILogger<Program> logger) =>
{
    if (request is null || request.Arguments is null)
    {
        return Results.BadRequest(CalculationResultBuilder.FromErrorMessage("A list of arguments is required in request body"));
    }
    var stackSizeBefore = stack.StackCalculatorSize;
    stack.PushArgumentsToStackCalculator(request.Arguments);
    var stackSizeAfter = stack.StackCalculatorSize;
    logger.LogInformation("Adding total of {argsLength} argument(s) to the stack | Stack size: {stackSizeAfter}", request.Arguments.Length, stackSizeAfter);
    logger.LogDebug("Adding arguments: {arguments} | Stack size before {stackSizeBefore} | stack size after {stackSizeAfter}", string.Join(',', request.Arguments), stackSizeBefore, stackSizeAfter);
    return Results.Ok(CalculationResultBuilder.FromResult(stackSizeAfter));
});
app.MapDelete("/calculator/stack/arguments", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "count")] int count, ICalculatorStack stack, ILogger<Program> logger) =>
{
    var stackCount = stack.StackCalculatorSize;
    if (!stack.TryPopStackCalculatorArguments(count, out var arguments))
    {
        var err = CalculationResultBuilder.FromErrorMessage($"Error: cannot remove {count} from the stack. It has only {stackCount} arguments");
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }
    Debug.Assert(arguments is not null);
    var stackSize = stack.StackCalculatorSize;
    logger.LogInformation("Removing total {removedArgsCount} argument(s) from the stack | Stack size: {stackSzie}", arguments.Length, stackSize);
    return Results.Ok(CalculationResultBuilder.FromResult(stackSize));
});
app.MapGet("/calculator/stack/operate", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "operation")] string? operationQuery,
            ICalculatorStack stack, ICalculator calculator, ICalculationHistory history, ILogger<Program> logger) =>
{
    if (operationQuery is null || !Enum.TryParse<Operation>(operationQuery, true, out var operation))
    {
        var err = CalculationResultBuilder.FromErrorMessage($"Error: unknown operation: {operationQuery}");
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }

    var stackCount = stack.StackCalculatorSize;
    var requiredAmount = calculator.GetRequriedArgumentsCount(operation);
    if (!stack.TryPopStackCalculatorArguments(requiredAmount, out var arguments))
    {
        var err = CalculationResultBuilder.FromErrorMessage($"Error: cannot implement operation {operationQuery}. It requires {requiredAmount} arguments and the stack has only {stackCount} arguments");
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }

    Debug.Assert(arguments is not null);
    if (!calculator.TryCalculate(operation, arguments, out var result, out var error))
    {
        Debug.Assert(error is not null);
        var err = CalculationResultBuilder.FromError(error.Value, operationQuery);
        logger.LogError("Server encountered an error ! message: {message}", err.ErrorMessage);
        return Results.Conflict(err);
    }

    Debug.Assert(result is not null);
    logger.LogInformation("Performing operation {operationName}. Result is {result} | stack size: {stackSize}", operationQuery, result, stack.StackCalculatorSize);
    logger.LogDebug("Performing operation: {operationName}({arguments}) = {result}", operationQuery, string.Join(',', arguments), result);
    history.Add(Flavor.Stack, operationQuery, arguments, result.Value);
    return Results.Ok(CalculationResultBuilder.FromResult(result.Value));
});

app.MapGet("/calculator/history", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "flavor")] string? flavorString, ICalculationHistory history, ILogger<Program> logger) =>
{
    if (flavorString is not null && !Flavor.Independent.Equals(flavorString) && !Flavor.Stack.Equals(flavorString))
    {
        var err = CalculationResultBuilder.FromErrorMessage($"Error: unknown flavor: {flavorString}");
        return Results.Conflict(err);
    }

    var calculations = history.CalculationsByFlavor(flavorString);
    if (flavorString is null || Flavor.Independent.Equals(flavorString))
    {
        using (LogContext.PushProperty("Logger", "independent-logger"))
        {
            logger.LogInformation("History: So far total {indiCalcCount} independent actions", calculations.Where(a => Flavor.Independent.Equals(a.Flavor)).Count());
        }
    }
    if (flavorString is null || Flavor.Stack.Equals(flavorString))
    {
        using (LogContext.PushProperty("Logger", "stack-logger"))
        {
            logger.LogInformation("History: So far total {stackCalcCount} stack actions", calculations.Where(a => Flavor.Stack.Equals(a.Flavor)).Count());
        }
    }

    return Results.Ok(CalculationResultBuilder.FromResult(calculations));
});

app.MapGet("/logs/level", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "logger-name")] string? loggerName, IDictionary<string, LoggingLevelSwitch> switchesDict, IDictionary<LogEventLevel, string> logLevelNameMap) =>
{
    if (string.IsNullOrWhiteSpace(loggerName))
    {
        return Results.BadRequest("logger-name is required.");
    }

    if (switchesDict.TryGetValue(loggerName, out var sw))
    {
        return Results.Ok(logLevelNameMap.TryGetValue(sw.MinimumLevel, out var name) ? name : sw.MinimumLevel.ToString().ToUpper());
    }

    return Results.Conflict($"{loggerName} doesn't exists.");
});
app.MapPut("/logs/level", ([Microsoft.AspNetCore.Mvc.FromQuery(Name = "logger-name")] string? loggerName, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "logger-level")] string? loggerLevel, IDictionary<string, LoggingLevelSwitch> switchesDict, IDictionary<string, LogEventLevel> logLevelMap) =>
{
    if (string.IsNullOrWhiteSpace(loggerName) || string.IsNullOrWhiteSpace(loggerLevel))
    {
        return Results.BadRequest("logger-name and logger-level are required.");
    }

    if (!logLevelMap.TryGetValue(loggerLevel, out var level))
    {
        return Results.Conflict($"Invalid logger level: {loggerLevel}.");
    }

    if (switchesDict.TryGetValue(loggerName, out var sw))
    {
        sw.MinimumLevel = level;
        return Results.Ok(loggerLevel);
    }

    return Results.Conflict($"{loggerName} doesn't exists.");
});


app.Run();

static class Flavor
{
    public const string Stack = "STACK";
    public const string Independent = "INDEPENDENT";
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
    public int[] Content { get; }
    public void PushArgumentsToStackCalculator(int[] arguments);
    public bool TryPopStackCalculatorArguments(int amount, out int[]? container);
}
interface ICalculationHistory
{
    void Add(string flavor, string operation, int[] arguments, int result);
    Calculation[] CalculationsByFlavor(string? flavor);
}
interface ICalculator
{
    bool TryCalculate(Operation operation, int[] arguments, out int? result, out CalculationError? error);
    int GetRequriedArgumentsCount(Operation operation);
}
interface IRequestCounter
{
    ulong Count { get; }
    ulong Increment();

}

class CalculatorStack : ICalculatorStack
{
    private readonly ConcurrentStack<int> argStack = new();

    public int StackCalculatorSize => argStack.Count;

    public int[] Content => argStack.ToArray();

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
    public void Add(string flavor, string operation, int[] arguments, int result)
    {
        var calculation = new Calculation(flavor, operation, arguments, result);
        switch (flavor)
        {
            case Flavor.Stack:
                stackHistory.Enqueue(calculation);
                break;
            case Flavor.Independent:
                independentHistory.Enqueue(calculation);
                break;
            default:
                throw new InvalidOperationException($"Cannot add calculation with flavor: {flavor}, did you forgot to add support for it?");
        }
    }
    public Calculation[] CalculationsByFlavor(string? flavor)
    {
        return flavor switch
        {
            Flavor.Stack => stackHistory.ToArray(),
            Flavor.Independent => independentHistory.ToArray(),
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
class RequestCounter : IRequestCounter
{
    ulong count;
    public RequestCounter()
    {
        count = 0;
    }

    public ulong Count => count;

    public ulong Increment()
    {
        return Interlocked.Increment(ref count);
    }
}
class RequestLoggerMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<RequestLoggerMiddleware> logger;

    public RequestLoggerMiddleware(RequestDelegate next, ILogger<RequestLoggerMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        using (LogContext.PushProperty("HttpVerb", context.Request.Method))
        {
            logger.LogInformation("Incoming request | #{RequestNumber} | resource: {RequestPath} | HTTP Verb {HttpVerb}");
        }
        var sw = Stopwatch.StartNew();
        await next(context);
        sw.Stop();
        using (LogContext.PushProperty("ResponseTime", sw.ElapsedMilliseconds))
        {
            logger.LogDebug("request #{RequestNumber} duration: {ResponseTime}ms");
        }
    }
}
class RequestNumberMiddleware
{
    private readonly IRequestCounter counter;
    private readonly RequestDelegate next;

    public RequestNumberMiddleware(RequestDelegate next, IRequestCounter counter)
    {
        this.next = next;
        this.counter = counter;
    }

    public async Task Invoke(HttpContext context)
    {
        var requestNumber = counter.Increment();
        using (LogContext.PushProperty("RequestNumber", requestNumber))
        {
            await next(context);
        }
    }
}
public class CustomLevelEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        string customLevel = logEvent.Level switch
        {
            LogEventLevel.Verbose => "TRACE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARN",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "CRITICAL",
            _ => logEvent.Level.ToString().ToUpper()
        };

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CustomLevel", customLevel));
    }
}
public static class LoggerEnrichmentConfigurationExtensions
{
    public static LoggerConfiguration WithCustomLevel(this LoggerEnrichmentConfiguration enrich)
    {
        return enrich.With<CustomLevelEnricher>();
    }
}
public static class StringExtensions
{
    public static string ToKababCase(this string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        if (text.Length < 2)
            return text.ToLowerInvariant();

        var sb = new StringBuilder();
        sb.Append(char.ToLowerInvariant(text[0]));

        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsUpper(c))
            {
                sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
