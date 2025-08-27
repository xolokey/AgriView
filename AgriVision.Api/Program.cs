using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
	using var ms = new MemoryStream();
	await stream.CopyToAsync(ms);
	var imageBytes = ms.ToArray();
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
		var http = new HttpClient();
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

		string base64 = Convert.ToBase64String(imageBytes);
		var payload = new
		{
			model = "gpt-4o-mini",
			messages = new object[]
			{
				new
				{
					role = "user",
					content = new object[]
					{
						new { type = "text", text = question },
						new { type = "image_url", image_url = new { url = $"data:{mediaType};base64,{base64}" } }
					}
				}
			}
		};

		var json = JsonSerializer.Serialize(payload);
		var response = await http.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));

		if ((int)response.StatusCode == 429 && mockMode)
		{
			string mockAnswer = BuildMockAnswer(question, file.FileName);
			return Results.Ok(new { answer = mockAnswer, note = "mock" });
		}

		response.EnsureSuccessStatusCode();
		var responseText = await response.Content.ReadAsStringAsync();

		using var doc = JsonDocument.Parse(responseText);
		string answer = ExtractAnswerFromChatCompletions(doc);
		return Results.Ok(new { answer });
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

static string ExtractAnswerFromChatCompletions(JsonDocument doc)
{
	try
	{
		var root = doc.RootElement;
		var choices = root.GetProperty("choices");
		if (choices.GetArrayLength() > 0)
		{
			var content = choices[0].GetProperty("message").GetProperty("content").GetString();
			return content ?? string.Empty;
		}
	}
	catch { }
	return string.Empty;
}

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
