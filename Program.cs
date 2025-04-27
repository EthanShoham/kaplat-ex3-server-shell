using System.ComponentModel.DataAnnotations;
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

app.MapPost("/calculator/independent/calculate", (CalculationRequest? request, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (request is null ||
        request.Arguments is null ||
        request.Operation is null ||
        !Enum.TryParse<Operation>(request.Operation, true, out var operation))
    {
        return Results.Conflict(CalculationResultBuilder.FromErrorMessage($"Error: unknown operation: {request?.Operation}"));
    }

    logger.LogInformation("Calc Request Operation: {Operation}, Args: {@Args}", request.Operation, request.Arguments);
    return Results.Ok(CalculationResultBuilder.FromResult(10));
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

class Calculator
{
    bool TryCalculate(List<int> arguments, Operation operation, out int? result, out string? errorMessage)
    {
        return (operation) switch
        {
            Operation.Plus => arguments.Count != 2 ? false :
        };
    }
}
record class CalculationRequest(List<int>? Arguments, string? Operation);
record class CalculationResult(int? Result, string? ErrorMessage);
static class CalculationResultBuilder
{
    public static CalculationResult FromResult(int result) => new CalculationResult(result, null);
    public static CalculationResult FromErrorMessage(string errorMessage) => new CalculationResult(null, errorMessage);
}

