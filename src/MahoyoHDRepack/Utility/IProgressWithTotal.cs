using System.Numerics;

namespace MahoyoHDRepack.Utility;

internal interface IProgressWithTotal<T> where T : INumber<T>
{
    T Total { get; set; }

    void AddProgress(T amount);
    void Complete() { }
    void SetStatusString(string statusString) { }
}
