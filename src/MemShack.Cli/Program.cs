using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await new MemShack.Cli.CliApp().RunAsync(args, Console.Out, Console.Error);
