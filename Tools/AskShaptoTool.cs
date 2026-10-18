using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// Asks Shapto a few questions at once, as a card with preset options and a free-text box, instead of guessing.
    /// The chat loop ends her turn right after this call; the answers come back as Shapto's next message.
    /// </summary>
    public sealed class AskShaptoTool : ICoreTool
    {
        public const string ToolName = "ask_shapto";
        private const int MaximumQuestions = 4;
        private const int MinimumOptions = 2;
        private const int MaximumOptions = 4;
        private const int MaximumQuestionCharacters = 300;
        private const int MaximumOptionCharacters = 80;

        public string Name => ToolName;

        public string Description =>
            "Ask Shapto 1 to 4 questions in one card when you're missing something you can't look up and a wrong guess would cost them time. " +
            "Whenever your question comes with choices (\"X, Y or Z?\"), put it in this card instead of writing the choices in your reply. " +
            "Give each question 2 to 4 short options; they can always type their own answer. Your reply ends when you call this, " +
            "and their answers come as their next message. Don't use it for things you can decide or check yourself.";

        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["questions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = MaximumQuestions,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["question"] = new JsonObject { ["type"] = "string", ["description"] = "One clear question." },
                            ["options"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject { ["type"] = "string" },
                                ["minItems"] = MinimumOptions,
                                ["maxItems"] = MaximumOptions,
                                ["description"] = "2 to 4 short answers to pick from. Put the one you'd recommend first.",
                            },
                            ["multi_select"] = new JsonObject { ["type"] = "boolean", ["description"] = "True if more than one option can apply." },
                        },
                        ["required"] = new JsonArray("question", "options"),
                    },
                },
            },
            ["required"] = new JsonArray("questions"),
        };

        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            ReadQuestions(arguments);
            return Task.FromResult("The question card is in front of Shapto. Stop here and don't answer for them; their answers come as their next message.");
        }

        /// <summary>
        /// The questions in the shape the page draws: [{ question, options: [...], multiSelect }].
        /// Throws ToolArgumentException with a fix-it message if they're malformed.
        /// </summary>
        public static JsonArray ReadQuestions(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("questions", out var questionsElement))
            {
                throw new ToolArgumentException("Missing \"questions\": a list of { question, options }.");
            }

            // Small models sometimes send the list as a JSON string instead of an array.
            using var parsedString = questionsElement.ValueKind == JsonValueKind.String ? TryParse(questionsElement.GetString()) : null;
            var questionList = parsedString?.RootElement ?? questionsElement;
            if (questionList.ValueKind != JsonValueKind.Array || questionList.GetArrayLength() == 0)
            {
                throw new ToolArgumentException("\"questions\" must be a list with 1 to 4 items.");
            }

            var questions = new JsonArray();
            foreach (var questionElement in questionList.EnumerateArray().Take(MaximumQuestions))
            {
                var questionText = ToolArguments.GetOptionalString(questionElement, "question")?.Trim() ?? "";
                if (questionText.Length == 0)
                {
                    throw new ToolArgumentException("Every item needs a \"question\".");
                }

                var options = ToolArguments.GetOptionalStringList(questionElement, "options")
                    .Select(option => option.Trim())
                    .Where(option => option.Length > 0)
                    .Distinct()
                    .Take(MaximumOptions)
                    .Select(option => option.Length > MaximumOptionCharacters ? option[..MaximumOptionCharacters] : option)
                    .ToList();
                if (options.Count < MinimumOptions)
                {
                    throw new ToolArgumentException($"\"{Shorten(questionText, 60)}\" needs at least 2 options to pick from.");
                }

                var multiSelect = questionElement.TryGetProperty("multi_select", out var multiSelectElement) && multiSelectElement.ValueKind == JsonValueKind.True;
                questions.Add(new JsonObject
                {
                    ["question"] = Shorten(questionText, MaximumQuestionCharacters),
                    ["options"] = new JsonArray(options.Select(option => (JsonNode)JsonValue.Create(option)!).ToArray()),
                    ["multiSelect"] = multiSelect,
                });
            }

            return questions;
        }

        public static JsonArray ReadQuestions(string argumentsText)
        {
            using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsText) ? "{}" : argumentsText);
            return ReadQuestions(argumentsDocument.RootElement);
        }

        /// <summary>
        /// What stays in the chat history for the model: the questions in plain text, so the next turn knows what the answers answer.
        /// </summary>
        public static string DescribeForHistory(JsonArray questions)
        {
            var builder = new StringBuilder("[You asked Shapto:");
            var questionNumber = 1;
            foreach (var question in questions)
            {
                var options = (question!["options"] as JsonArray ?? [])
                    .Select(option => option!.GetValue<string>());
                builder.Append($"\n{questionNumber}. {question["question"]!.GetValue<string>()} (options: {string.Join(" / ", options)})");
                questionNumber++;
            }

            return builder.Append(']').ToString();
        }

        private static JsonDocument? TryParse(string? text)
        {
            try
            {
                return text is null ? null : JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Shorten(string text, int maximumCharacters)
        {
            return text.Length > maximumCharacters ? text[..maximumCharacters].TrimEnd() + "…" : text;
        }
    }
}