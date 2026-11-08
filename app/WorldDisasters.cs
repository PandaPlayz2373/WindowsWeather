namespace WindowsWeather;

// What the disasters (tornado, earthquake, flood) share: the per-effect modifiers, whether any of them is busy or
// visible, and ending them all at once.
public sealed partial class World
{
    /// <summary>Per-effect modifiers from the effect picker ("Storm.Frequency" etc.), multipliers around 1.</summary>
    public Dictionary<string, float> Mods = new();

    public float Mod(string key) => Mods.TryGetValue(key, out var v) ? Math.Clamp(v, 0.1f, 2f) : 1f;

    bool DisasterBusy => _qOn || _flLevel > 0 || _tiles.Count > 0 || _cracks.Count > 0 || _remaxAt > 0;

    bool DisasterVisible(Monitor m) => (_flLevel > 0) || (_tiles.Count > 0 && _tbMon == m) || (_cracks.Count > 0 && _crackMon == m);

    /// <summary>Stop every disaster now and put back what they changed (pause, quit).</summary>
    void EndDisasters()
    {
        _qOn = false; QuakeAmp = 0;
        _remaxAt = Now; Remaximize();
        _flLevel = 0; _flStage = 0;
        SnapTilesHome();
        _cracks.Clear();
    }
}
