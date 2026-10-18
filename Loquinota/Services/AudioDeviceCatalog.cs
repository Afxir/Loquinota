using Loquinota.Helpers;
using Loquinota.Models;
using NAudio.CoreAudioApi;

namespace Loquinota.Services;

/// <summary>
/// Lists WASAPI capture (microphone) and render (speaker/loopback) devices.
/// Implemented by the audio workstream.
/// </summary>
public static class AudioDeviceCatalog
{
    /// <summary>Returns active capture endpoints, default first.</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        var microphones = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // Default communications endpoint wins, then the default multimedia endpoint.
            var defaultEndpoint = TryGetDefaultEndpoint(enumerator, DataFlow.Capture, Role.Communications)
                                  ?? TryGetDefaultEndpoint(enumerator, DataFlow.Capture, Role.Multimedia);

            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            for (var i = 0; i < collection.Count; i++)
            {
                MMDevice? device = null;
                try
                {
                    device = collection[i];
                    var id = device.ID;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var name = SafeFriendlyName(device);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = $"Microphone {i + 1}";
                    }

                    microphones.Add(new AudioDeviceInfo
                    {
                        Id = id,
                        Name = name,
                        IsDefault = defaultEndpoint is { } def &&
                                    string.Equals(id, def.Id, StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not read capture endpoint #{i}.", ex);
                }
                finally
                {
                    device?.Dispose();
                }
            }

            // The default endpoint should normally be part of the active list; add it if it is not.
            if (defaultEndpoint is { } fallback && !microphones.Any(m => m.IsDefault))
            {
                microphones.Insert(0, new AudioDeviceInfo
                {
                    Id = fallback.Id,
                    Name = string.IsNullOrWhiteSpace(fallback.Name) ? "Default microphone" : fallback.Name,
                    IsDefault = true,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Enumerating capture devices failed.", ex);
        }

        // Stable sort: default endpoint first, the rest in enumeration order.
        return microphones.OrderByDescending(m => m.IsDefault).ToList();
    }

    /// <summary>Returns the default communications capture endpoint, or null.</summary>
    public static AudioDeviceInfo? GetDefaultMicrophone()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = TryGetDefaultEndpoint(enumerator, DataFlow.Capture, Role.Communications)
                           ?? TryGetDefaultEndpoint(enumerator, DataFlow.Capture, Role.Multimedia);
            if (endpoint is not { } value)
            {
                return null;
            }

            return new AudioDeviceInfo
            {
                Id = value.Id,
                Name = string.IsNullOrWhiteSpace(value.Name) ? "Default microphone" : value.Name,
                IsDefault = true,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the default microphone failed.", ex);
            return null;
        }
    }

    /// <summary>Returns the default render endpoint name (used by loopback capture), or null.</summary>
    public static string? GetDefaultSpeakerName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // Match NAudio's WasapiLoopbackCapture default: multimedia render endpoint.
            var endpoint = TryGetDefaultEndpoint(enumerator, DataFlow.Render, Role.Multimedia)
                           ?? TryGetDefaultEndpoint(enumerator, DataFlow.Render, Role.Communications);
            var name = endpoint?.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the default speaker failed.", ex);
            return null;
        }
    }

    private static (string Id, string Name)? TryGetDefaultEndpoint(
        MMDeviceEnumerator enumerator,
        DataFlow dataFlow,
        Role role)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(dataFlow, role))
            {
                return null;
            }

            using var device = enumerator.GetDefaultAudioEndpoint(dataFlow, role);
            var id = device.ID;
            return string.IsNullOrWhiteSpace(id) ? null : (id, SafeFriendlyName(device));
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the default {dataFlow} endpoint ({role}) failed.", ex);
            return null;
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading an audio endpoint name failed.", ex);
            return string.Empty;
        }
    }
}
