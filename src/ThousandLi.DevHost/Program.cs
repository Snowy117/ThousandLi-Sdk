using ThousandLi.DevHost;

var options = DevHostOptions.Parse(args);
var app = await DevHostApplication.BuildAsync(options, args).ConfigureAwait(false);
Console.WriteLine($"ThousandLi DevHost: http://127.0.0.1:{options.Port}");
Console.WriteLine(options.Ephemeral ? "Storage: ephemeral" : "Storage: persistent");
await app.RunAsync().ConfigureAwait(false);
