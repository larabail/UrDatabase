using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using UrDatabase.Models;

namespace UrDatabase.Services;

internal static class FfprobeMediaInfo
{
    public static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!Property(root, "streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ffprobe returned no stream array.");

        var info = new MediaInfo();
        JsonElement? video = null;
        JsonElement? audio = null;
        var trackCount = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            switch (Text(stream, "codec_type"))
            {
                case "video" when !Disposition(stream, "attached_pic"):
                    trackCount++;
                    if (video is null || !Disposition(video.Value, "default") && Disposition(stream, "default"))
                        video = stream;
                    break;
                case "audio":
                    trackCount++;
                    info.AudioLanguages.Add(Language(stream));
                    if (audio is null || !Disposition(audio.Value, "default") && Disposition(stream, "default"))
                        audio = stream;
                    break;
                case "subtitle":
                    trackCount++;
                    info.SubtitleLanguages.Add(Language(stream));
                    break;
            }
        }
        if (trackCount == 0) throw new InvalidDataException("ffprobe returned no media tracks.");

        if (video is { } picture)
        {
            info.Width = PositiveInt(picture, "width");
            info.Height = PositiveInt(picture, "height");
            info.VideoCodec = Text(picture, "codec_name");
            info.VideoRange = DynamicRange(picture);
        }
        if (audio is { } sound)
        {
            info.AudioCodec = Text(sound, "codec_name");
            info.AudioChannels = PositiveInt(sound, "channels");
            info.HasAtmos = Text(sound, "profile")?.Contains("Dolby Atmos", StringComparison.OrdinalIgnoreCase) == true;
        }
        if (Property(root, "format", out var format))
            info.Container = Text(format, "format_name");
        return info;
    }

    private static string Language(JsonElement stream)
    {
        if (Property(stream, "tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase) &&
                    tag.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(tag.Value.GetString()))
                    return tag.Value.GetString()!.Trim();
        }
        return "und";
    }

    private static string? DynamicRange(JsonElement video)
    {
        if (Property(video, "side_data_list", out var data) && data.ValueKind == JsonValueKind.Array &&
            data.EnumerateArray().Any(item =>
                Text(item, "side_data_type") == "DOVI configuration record" && PositiveInt(item, "dv_profile") is > 0))
            return "DOVI";

        // PQ alone proves HDR, not HDR10/HDR10+ mastering or Dolby Vision.
        return Text(video, "color_transfer") switch
        {
            "smpte2084" => "HDR",
            "arib-std-b67" => "HLG",
            _ => null
        };
    }

    private static bool Disposition(JsonElement stream, string name) =>
        Property(stream, "disposition", out var disposition) && PositiveInt(disposition, name) == 1;

    private static int? PositiveInt(JsonElement value, string name) =>
        Property(value, name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var number) && number > 0 ? number : null;

    private static string? Text(JsonElement value, string name) =>
        Property(value, name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()!.Trim() : null;

    private static bool Property(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property);
    }
}
