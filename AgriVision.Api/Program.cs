using Microsoft.AspNetCore.Http;
using OpenAI.Chat;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var allowFrontend = "AllowFrontend";
builder.Services.AddCors(options =>
{
	options.AddPolicy(name: allowFrontend, policy =>
	{
		policy.WithOrigins("http://localhost:5173")
			.AllowAnyHeader()
			.AllowAnyMethod();
	});
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseCors(allowFrontend);
app.UseDefaultFiles();
app.UseStaticFiles();

// Log whether an API key is visible at startup (without revealing it)
{
	bool hasEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
	bool hasCfgRoot = !string.IsNullOrWhiteSpace(builder.Configuration["OPENAI_API_KEY"]);
	bool hasCfgSection = !string.IsNullOrWhiteSpace(builder.Configuration["OpenAI:ApiKey"]);
	bool configured = hasEnv || hasCfgRoot || hasCfgSection;
	app.Logger.LogInformation("OpenAI API key configured at startup: {Configured}", configured);
}

// Health endpoint to check key visibility at runtime
app.MapGet("/healthz", (IConfiguration cfg) =>
{
	bool hasEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
	bool hasCfgRoot = !string.IsNullOrWhiteSpace(cfg["OPENAI_API_KEY"]);
	bool hasCfgSection = !string.IsNullOrWhiteSpace(cfg["OpenAI:ApiKey"]);
	return Results.Ok(new { openaiKeyConfigured = hasEnv || hasCfgRoot || hasCfgSection, hasEnv, hasCfgRoot, hasCfgSection });
});

app.MapPost("/api/analyze", async (HttpRequest request) =>
{
	if (!request.HasFormContentType)
	{
		return Results.BadRequest("Multipart form-data with 'image' and 'question' is required.");
	}

	var form = await request.ReadFormAsync();
	var file = form.Files["image"];
	var question = form["question"].ToString();
	if (file is null || file.Length == 0)
	{
		return Results.BadRequest("Image file 'image' is required.");
	}
	if (string.IsNullOrWhiteSpace(question))
	{
		question = "Describe this image.";
	}

	await using var stream = file.OpenReadStream();
	var imageBytes = BinaryData.FromStream(stream);
	var mediaType = file.ContentType ?? "image/png";

	bool mockMode = string.Equals(Environment.GetEnvironmentVariable("AGRI_MOCK_MODE"), "true", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(builder.Configuration["AGRI_MOCK_MODE"], "true", StringComparison.OrdinalIgnoreCase);

	var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
		?? builder.Configuration["OPENAI_API_KEY"]
		?? builder.Configuration["OpenAI:ApiKey"];
	if (string.IsNullOrWhiteSpace(apiKey) && !mockMode)
	{
		return Results.Problem("OPENAI_API_KEY is not configured.", statusCode: 500);
	}

	// If mock mode is enabled, short-circuit with a friendly templated answer
	if (mockMode)
	{
		string mockAnswer = BuildMockAnswer(question, file.FileName);
		return Results.Ok(new { answer = mockAnswer });
	}

	try
	{
		var client = new ChatClient("gpt-4o", apiKey);
		var messages = new List<ChatMessage>
		{
			new UserChatMessage(
				ChatMessageContentPart.CreateTextPart(question),
				ChatMessageContentPart.CreateImagePart(imageBytes, mediaType))
		};

		ChatCompletion completion = await client.CompleteChatAsync(messages);
		var answer = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
		return Results.Ok(new { answer });
	}
	catch (ClientResultException ex)
	{
		// Gracefully handle OpenAI API errors such as insufficient_quota (HTTP 429)
		var message = ex.Message ?? "Upstream AI service error.";
		// Prefer 429 if indicated, otherwise 502 Bad Gateway
		var statusCode = message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) ? 429 : 502;
		if (statusCode == 429 && mockMode)
		{
			string mockAnswer = BuildMockAnswer(question, file.FileName);
			return Results.Ok(new { answer = mockAnswer, note = "mock" });
		}
		return Results.Problem(
			detail: statusCode == 429
				? "Quota exceeded. Please check your plan and billing details."
				: "The AI service is unavailable right now. Please try again shortly.",
			statusCode: statusCode,
			title: statusCode == 429 ? "Insufficient quota" : "Upstream service error");
	}
	catch (Exception ex)
	{
		return Results.Problem(
			detail: ex.Message,
			statusCode: 500,
			title: "Unexpected server error");
	}
})
.DisableAntiforgery();

app.MapFallbackToFile("/index.html");

app.Run();

static string BuildMockAnswer(string question, string fileName)
{
	// Very simple heuristic mock for demonstration
	var normalized = (question ?? string.Empty).Trim().ToLowerInvariant();
	if (normalized.Contains("what crop") && normalized.Contains("issues"))
	{
		return "Crop: Wheat. Observed issues: mild leaf rust spots and slight nitrogen deficiency (yellowing lower leaves). Suggested actions: apply a labeled fungicide if disease pressure increases, and consider a split nitrogen top-dress aligned with local recommendations.";
	}
	if (normalized.Contains("what crop"))
	{
		return "Likely crop: Wheat, based on general morphology. For precise ID, provide a closer view of leaves and seed heads.";
	}
	if (normalized.Contains("issues") || normalized.Contains("problems"))
	{
		return "Potential issues: minor foliar disease and uneven nutrition. Recommend scouting multiple spots, checking soil moisture, and conducting a quick soil test to fine-tune fertilization.";
	}
	return $"Mock analysis for '{fileName}': healthy stand with uniform tillering; no major stress detected. Ask a more specific question for targeted advice.";
}
