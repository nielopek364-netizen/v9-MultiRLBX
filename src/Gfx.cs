using System;
using System.Collections.Generic;
using System.Text;

namespace MultiRLBX
{
    /// <summary>
    /// Builds allowlisted Roblox <c>ClientAppSettings.json</c> Fast Flag payloads from
    /// graphics presets and user overrides.
    /// </summary>
    public static class Gfx
    {
        public static readonly string[] Allowlist = new string[]
        {
            "DFIntCSGLevelOfDetailSwitchingDistance",
            "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23",
            "DFIntCSGLevelOfDetailSwitchingDistanceL34",
            "FFlagHandleAltEnterFullscreenManually",
            "DFFlagTextureQualityOverrideEnabled",
            "DFIntTextureQualityOverride",
            "FIntDebugForceMSAASamples",
            "DFFlagDisableDPIScale",
            "FFlagDebugGraphicsPreferD3D11",
            "FFlagDebugSkyGray",
            "DFFlagDebugPauseVoxelizer",
            "DFIntDebugFRMQualityLevelOverride",
            "FIntFRMMaxGrassDistance",
            "FIntFRMMinGrassDistance",
            "FFlagDebugGraphicsPreferVulkan",
            "FFlagDebugGraphicsPreferOpenGL",
            "FIntGrassMovementReducedMotionFactor"
        };

        static readonly HashSet<string> allow = new HashSet<string>(Allowlist);

        /// <summary>Returns <c>true</c> if the specified Fast Flag is on Roblox's local allowlist.</summary>
        public static bool IsAllowed(string flag) { return allow.Contains(flag); }

        /// <summary>Populates <see cref="GfxSettings"/> fields according to the chosen preset key.</summary>
        public static void ApplyPreset(GfxSettings g, string key)
        {
            if (g == null) return;
            if (key == "Custom")
            {
                g.Preset = "Custom";
                return;
            }
            g.Quality = 0;
            g.Texture = -1;
            g.Msaa = 0;
            g.NoGrass = false;
            g.PauseVoxelizer = false;
            g.GraySky = false;
            g.LowCsg = false;
            if (key == "Light" || key == "Low")
            {
                g.Texture = 2;
                g.Msaa = 1;
            }
            else if (key == "LowCpu" || key == "Potato")
            {
                g.Quality = 3;
                g.Texture = 1;
                g.Msaa = 1;
                g.NoGrass = true;
                if (key == "Potato") g.GraySky = true;
            }
            else if (key == "Lowest" || key == "Ultrapotato")
            {
                g.Quality = 1;
                g.Texture = 0;
                g.Msaa = 1;
                g.NoGrass = true;
                g.PauseVoxelizer = true;
                g.GraySky = true;
                g.LowCsg = true;
            }
            g.Preset = string.IsNullOrEmpty(key) ? "Default" : key;
        }

        // Flags coming from the options on the Performance page.
        public static Dictionary<string, object> Managed(GfxSettings g)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            if (g.Quality > 0) d["DFIntDebugFRMQualityLevelOverride"] = g.Quality.ToString();
            if (g.Texture >= 0)
            {
                d["DFFlagTextureQualityOverrideEnabled"] = "True";
                d["DFIntTextureQualityOverride"] = g.Texture.ToString();
            }
            if (g.Msaa > 0) d["FIntDebugForceMSAASamples"] = g.Msaa.ToString();
            if (g.NoGrass)
            {
                d["FIntFRMMinGrassDistance"] = "0";
                d["FIntFRMMaxGrassDistance"] = "0";
            }
            if (g.PauseVoxelizer) d["DFFlagDebugPauseVoxelizer"] = "True";
            if (g.GraySky) d["FFlagDebugSkyGray"] = "True";
            if (g.LowCsg)
            {
                d["DFIntCSGLevelOfDetailSwitchingDistance"] = "0";
                d["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = "0";
                d["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = "0";
                d["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = "0";
            }
            if (g.Api == "D3D11") d["FFlagDebugGraphicsPreferD3D11"] = "True";
            else if (g.Api == "Vulkan") d["FFlagDebugGraphicsPreferVulkan"] = "True";
            else if (g.Api == "OpenGL") d["FFlagDebugGraphicsPreferOpenGL"] = "True";
            return d;
        }

        // Everything that will really be written: managed flags + the user's own flags (filtered).
        public static Dictionary<string, object> Final(out List<string> ignored)
        {
            ignored = new List<string>();
            GfxSettings g = Store.Settings.Gfx;
            Dictionary<string, object> d = Managed(g);
            string custom = Store.Settings.CustomClientSettings;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    IDictionary<string, object> c = Json.Obj(Json.Parse(custom));
                    if (c == null) ignored.Add("(custom flags must be a JSON object like {\"FlagName\": \"True\"})");
                    else
                        foreach (KeyValuePair<string, object> kv in c)
                        {
                            if (IsAllowed(kv.Key) || g.WriteUnlisted) d[kv.Key] = kv.Value;
                            else ignored.Add(kv.Key);
                        }
                }
                catch (Exception) { ignored.Add("(custom flags are not valid JSON)"); }
            }
            return d;
        }

        static string Pretty(Dictionary<string, object> d)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{").Append(Environment.NewLine);
            int i = 0;
            foreach (KeyValuePair<string, object> kv in d)
            {
                i++;
                sb.Append("  \"").Append(kv.Key).Append("\": ").Append(Json.To(kv.Value));
                if (i < d.Count) sb.Append(",");
                sb.Append(Environment.NewLine);
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static string Preview()
        {
            List<string> ignored;
            Dictionary<string, object> f = Final(out ignored);
            StringBuilder sb = new StringBuilder();
            if (f.Count == 0) sb.AppendLine("(nothing - Roblox runs with its own default settings)");
            else sb.AppendLine(Pretty(f));
            foreach (string s in ignored) sb.AppendLine("NOT written (not on Roblox's list): " + s);
            return sb.ToString();
        }
    }
}
