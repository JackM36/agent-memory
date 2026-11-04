using System.Text;
using AgentBrain.Cli;

// Output is UTF-8 everywhere; budgets are measured in UTF-8 bytes.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await BrainCommandLine.Create().Parse(args).InvokeAsync();
