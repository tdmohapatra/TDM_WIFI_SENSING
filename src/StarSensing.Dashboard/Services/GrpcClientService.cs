using Grpc.Core;
using Grpc.Net.Client;
using StarSensing.Core.Protos;

namespace StarSensing.Dashboard.Services;

public class GrpcClientService
{
    private GrpcChannel? _channel;
    public SensingService.SensingServiceClient? Client { get; private set; }

    public void Connect(string url = "http://localhost:5050")
    {
        _channel = GrpcChannel.ForAddress(url);
        Client = new SensingService.SensingServiceClient(_channel);
    }
}
