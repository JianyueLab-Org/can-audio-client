using System.Text.Json;
using System.Text.Json.Nodes;
using ControllerClient.ViewModels;

namespace ControllerClient.Services;

public static class SettingsStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAN", "AudioClient");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static void Load(SettingsViewModel model)
    {
        var path = File.Exists(FilePath) ? FilePath : Path.Combine(VoiceBridge.Root, "controller", "radio_settings.json");
        if (!File.Exists(path)) { model.MarkSaved(); return; }
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            model.NetworkId = (string?)json["last_username"] ?? "";
            var host = (string?)json["mumble_host"] ?? SettingsViewModel.DefaultServerHost;
            if (host is "hjdczy.top" or "audio.airwaysn.org") host = SettingsViewModel.DefaultServerHost;
            // 不开调试端点时只加载 CAN 自己的服务器（例如服务器列表里选的杭州），其余一律用默认
            if (model.ShowServerEndpoints || SettingsViewModel.IsOfficialServer(host))
            {
                model.ServerHost = host;
                model.ServerPort = (int?)json["mumble_port"] ?? SettingsViewModel.DefaultServerPort;
            }
            model.OutputVolume = 20 * Math.Log10(Math.Max(1, (double?)json["speaker_volume"] ?? 100) / 100);
            model.InputDeviceName = (string?)json["input_name"] ?? "系统默认";
            model.HeadsetDeviceName = (string?)json["output_name"] ?? "系统默认";
            model.SpeakerDeviceName = (string?)json["speaker_name"] ?? "系统默认";
            model.DisableAudioEffects = !((bool?)json["mic_denoise"] ?? true);
            model.PttBindings = json["ptt_bindings"]?.Deserialize<JsonElement[]>() ??
                [JsonSerializer.SerializeToElement(new { kind = "keyboard", key = (string?)json["ptt_key"] ?? "v" })];
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { model.StatusText = "配置读取失败：" + ex.Message; }
        model.MarkSaved();
    }

    public static void Save(SettingsViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.ServerHost) || model.ServerPort is < 1 or > 65535)
            throw new InvalidOperationException("请填写正确的语音服务器地址和端口。");
        Directory.CreateDirectory(DirectoryPath);
        var data = new { last_username = model.NetworkId, mumble_host = model.ServerHost, mumble_port = model.ServerPort,
            speaker_volume = 100 * Math.Pow(10, model.OutputVolume / 20), mic_denoise = !model.DisableAudioEffects,
            input_name = model.InputDeviceName, output_name = model.HeadsetDeviceName, speaker_name = model.SpeakerDeviceName,
            ptt_bindings = model.PttBindings };
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        AppLog.Info("设置已保存");
        model.MarkSaved();
    }
}
