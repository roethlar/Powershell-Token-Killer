using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;

var configurationPath = Environment.GetEnvironmentVariable("PTK_SIEM_CONFIG");
if (string.IsNullOrWhiteSpace(configurationPath))
{
    Console.Error.WriteLine(
        "siem_receiver_configuration_invalid: config_env — set PTK_SIEM_CONFIG to " +
        "the fully qualified path of the receiver configuration JSON file.");
    return 1;
}

SiemReceiverOptions options;
try
{
    options = SiemReceiverConfigurationLoader.Load(configurationPath);
}
catch (SiemReceiverConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

try
{
    await using var application = ReceiverApplication.Build(options, args);
    await application.RunAsync();
    return 0;
}
catch (SiemReceiverStartupException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
