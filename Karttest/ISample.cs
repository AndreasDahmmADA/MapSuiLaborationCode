using Mapsui;

namespace Karttest;

public interface ISample
{
    Task<Map> CreateMapAsync();
}