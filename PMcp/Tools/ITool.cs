using System.Collections.Generic;
using PMcp.Util;

namespace PMcp.Tools;

public interface ITool
{
    string Name { get; }
    string Title { get; }
    string Description { get; }
    JsonObject InputSchema { get; }

    object Execute(Dictionary<string, object?> args);
}

public interface IWriteTool : ITool { }
