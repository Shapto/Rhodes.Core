using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhodes.Core.Tools
{
    /// <summary>
    /// A tool Closure can call. Name, Description and ParametersSchema become the OpenAI-style
    /// function definition; they must come out identical every turn so LM Studio can cache the prompt.
    /// </summary>
    public interface ICoreTool
    {
        string Name { get; }

        string Description { get; }

        JsonObject ParametersSchema { get; }

        /// <summary>
        /// Runs the tool and returns plain text for the model. Throws ToolArgumentException
        /// when the arguments are wrong; its message is shown to her so she can retry.
        /// </summary>
        Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A mistake in the model's arguments. The message is written for the model: what was wrong and what to do instead.
    /// </summary>
    public sealed class ToolArgumentException(string message) : Exception(message);
}
