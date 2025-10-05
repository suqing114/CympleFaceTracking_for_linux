using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

using VRCFaceTracking;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Types;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using System.Drawing.Imaging;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;


namespace CympleFaceTracking
{
    public class CympleFaceTrackingModule : ExtTrackingModule
    {
        static int FLAG_MOUTH_E = 0x01;
        static int FLAG_EYE_E = 0x02;
    private UdpClient? _CympleFaceConnection;
    private IPEndPoint? _CympleFaceRemoteEndpoint;
    private CympleFaceDataStructs? _latestData;
        private (bool, bool) trackingSupported = (false, false);
        private volatile bool _isExiting = false;
        private bool disconnectWarned = false;
    private Thread? _thread;
        private (bool, bool) validateModule = (false, false);
        

        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);
        // This is the first function ran by VRCFaceTracking. Make sure to completely initialize 
        // your tracking interface or the data to be accepted by VRCFaceTracking here. This will let 
        // VRCFaceTracking know what data is available to be sent from your tracking interface at initialization.
        // Simple cross-platform INI parser for small config files
        // Returns true if the key exists and is "1" or "true" (case-insensitive)
        public bool GetBoolValue(string filePath, string section, string key, bool defaultValue = false)
        {
            try
            {
                if (!File.Exists(filePath)) return defaultValue;
                string currentSection = string.Empty;
                foreach (var raw in File.ReadLines(filePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase)) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var k = line.Substring(0, idx).Trim();
                    var v = line.Substring(idx + 1).Trim();
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    {
                        v = v.Trim('"');
                        v = v.ToLowerInvariant();
                        if (v == "1" || v == "true") return true;
                        if (v == "0" || v == "false") return false;
                        return defaultValue;
                    }
                }
            }
            catch
            {
                // ignore parse errors and return default
            }
            return defaultValue;
        }

        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation.Name = "CympleFaceTracking";
            System.Reflection.Assembly a = System.Reflection.Assembly.GetExecutingAssembly();
            var img = a.GetManifestResourceStream("CympleFaceTracking.Assets.logo.png");
            Logger.LogInformation("Initializing CympleFaceTracking module.");

            // UDP client setup (guard against port already in use)
            try
            {
                _CympleFaceConnection = new UdpClient(Constants.Port);
                // 修改UDP客户端初始化
                _CympleFaceConnection.Client.ReceiveTimeout = 1000; // 设置1秒超时
                // bind port
                _CympleFaceRemoteEndpoint = new IPEndPoint(IPAddress.Any, Constants.Port);
            }
            catch (SocketException se)
            {
                // Address already in use or other socket error - log and return unsupported
                Logger.LogWarning($"CympleFace UDP port {Constants.Port} unavailable: {se.Message}");
                trackingSupported = (false, false);
                return trackingSupported;
            }

            _latestData = new CympleFaceDataStructs();

            // Build ini path dynamically from current user's home directory
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            string iniDir;
            if (string.IsNullOrEmpty(home))
            {
                Logger.LogError("Could not determine user home directory for INI path");
                iniDir = Path.Combine(".wine", "drive_c", "Cymple", "iniFile.ini");
            }
            else
            {
                iniDir = Path.Combine(home, ".wine", "drive_c", "Cymple", "iniFile.ini");
            }
            bool eyeEnabled = false;
            bool lipEnabled = false;
            if (!File.Exists(iniDir))
            {
                Logger.LogError("Cymple face INI file not found");
            }
            else{
                eyeEnabled = GetBoolValue(iniDir, "Function Switch", "cymple_eye_sw");
                lipEnabled = GetBoolValue(iniDir, "Function Switch", "cymple_mouth_sw");
            }
            Logger.LogInformation($"CympleFace module eye: {eyeEnabled} mouth: {lipEnabled}");
            trackingSupported = (eyeEnabled, lipEnabled);
            ModuleInformation.Name = "Cymple Facial Tracking V1.2.2";
            if (img != null)
            {
                List<Stream> streams = new List<Stream>();
                streams.Add(img);
                ModuleInformation.StaticImages = streams;
            }
            else
            {
                Logger.LogError("Could not find logo.");
            }
            _thread = new Thread(ReadLoop);
            _thread.Start();
            return trackingSupported;
        }
        private void ReadLoop()
        {
            Logger.LogInformation("CympleFace readLoop start.");
            while (!_isExiting)
            {
                // Read data from UDP
                try
                {
                    // Grab the packet - will block but with a timeout set in the init function
                    if (_CympleFaceConnection == null || _CympleFaceRemoteEndpoint == null)
                    {
                        Logger.LogWarning("UDP connection or endpoint is null in read loop");
                        Thread.Sleep(50);
                        continue;
                    }
                    Byte[] receiveBytes = _CympleFaceConnection.Receive(ref _CympleFaceRemoteEndpoint);

                    if (receiveBytes.Length < 12) // At least prefix, flags, type, length
                    {
                        continue;
                    }

                    // Connection status handling
                    if (disconnectWarned)
                    {
                        Logger.LogInformation("cympleFace connection reestablished");
                        disconnectWarned = false;
                    }

                    // Read header fields with new format
                    int prefix = BitConverter.ToInt32(receiveBytes, 0);
                    uint flags = BitConverter.ToUInt32(receiveBytes, 4);
                    // Convert endianness if needed (assuming network byte order is big-endian)
                    if (BitConverter.IsLittleEndian)
                    {
                        flags = (uint)((flags & 0xFF) << 24 | (flags & 0xFF00) << 8 | (flags & 0xFF0000) >> 8 | (flags & 0xFF000000) >> 24);
                    }
                    ushort type = BitConverter.ToUInt16(receiveBytes, 8);
                    short length = BitConverter.ToInt16(receiveBytes, 10);

                    // Verify message prefix and type
                    if (prefix != Constants.MSG_PREFIX || type != Constants.OSC_MSG_BLENDSHAPEDATA)
                    {
                        Logger.LogWarning($"Invalid message: prefix={prefix:X}, type={type}");
                        continue;
                    }

                    // Set flags
                    if (_latestData == null)
                    {
                        _latestData = new CympleFaceDataStructs();
                    }
                    _latestData.Flags = flags;

                    // Use a more efficient approach to read all blendshape values
                    ReadBlendshapeValues(receiveBytes, ref _latestData);
                }
                catch (SocketException se)
                {
                    HandleSocketException(se);
                }
                catch (Exception e)
                {
                    // some other exception
                    Logger.LogError(e.ToString());
                }
            }
            Logger.LogInformation("CympleFace readLoop end.");
        }
        // Polls data from the tracking interface.
        // VRCFaceTracking will run this function in a separate thread;
        public override void Update()
        {
            if (_isExiting) return;
            
            if (_CympleFaceConnection == null)
            {
                Logger.LogError("CympleFace connection is null");
                return;
            }

            // Update expressions with the latest data
            UpdateExpressions();
            
            // Add a small sleep to prevent CPU hogging, similar to ETVRTrackingModule
            Thread.Sleep(5);
        }
        private void UpdateExpressions()
        {
            // Update eye tracking data
            if (_latestData == null) return;
            if ((_latestData.Flags & FLAG_EYE_E) != 0)
            {
                if (false == validateModule.Item1)
                {
                    validateModule.Item1 = true;
                    Logger.LogInformation("eye tracking activated");
                }
                else
                {
                    UpdateEyeData();
                }
            }
            
            // Update facial expressions
            if ((_latestData.Flags & FLAG_MOUTH_E) != 0)
            {
                if (false == validateModule.Item2)
                {
                    validateModule.Item2 = true;
                    Logger.LogInformation("facial expression tracking activated");
                }
                else
                {
                    UpdateFacialExpressions();
                }
            }
        }

        private void UpdateEyeData()
        {
            var latest = _latestData;
            if (latest == null) return;

            UnifiedTracking.Data.Eye.Left.Gaze = new Vector2(latest.EyeYaw_L, latest.EyePitch);
            UnifiedTracking.Data.Eye.Right.Gaze = new Vector2(latest.EyeYaw_R, latest.EyePitch);

            // Use raw eye openness values
            UnifiedTracking.Data.Eye.Left.Openness = 1.0f - latest.EyeLidCloseLeft;
            UnifiedTracking.Data.Eye.Right.Openness = 1.0f - latest.EyeLidCloseRight;

            // Pupil dilation - raw values
            UnifiedTracking.Data.Eye._minDilation = 0;
            UnifiedTracking.Data.Eye._maxDilation = 10;
            UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 5.0f + latest.Eye_Pupil_Left * 5.0f;
            UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = 5.0f + latest.Eye_Pupil_Right * 5.0f;

            // Eye squint - raw values
            UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeSquintLeft].Weight = latest.EyeSquintLeft;
            UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeSquintRight].Weight = latest.EyeSquintRight;
        }

        private void UpdateFacialExpressions()
        {
            var shapes = UnifiedTracking.Data.Shapes;
            var latest = _latestData;
            if (latest == null) return;

            // Cheek region
            UpdateCheekExpressions(ref shapes);

            // Lip region
            UpdateLipExpressions(ref shapes);

            // Mouth region
            UpdateMouthExpressions(ref shapes);

            // Tongue region
            UpdateTongueExpressions(ref shapes);
        }

        private void UpdateCheekExpressions(ref UnifiedExpressionShape[] shapes)
        {
            var latest = _latestData;
            if (latest == null) return;

            shapes[(int)UnifiedExpressions.CheekPuffLeft].Weight = latest.CheekPuffLeft;
            shapes[(int)UnifiedExpressions.CheekPuffRight].Weight = latest.CheekPuffRight;
            shapes[(int)UnifiedExpressions.CheekSuckLeft].Weight = shapes[(int)UnifiedExpressions.CheekSuckRight].Weight = latest.CheekSuck;
        }

        private void UpdateLipExpressions(ref UnifiedExpressionShape[] shapes)
        {
            // Lip suck
            var latest = _latestData;
            if (latest == null) return;

            shapes[(int)UnifiedExpressions.LipSuckUpperLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipSuckUpperRight].Weight = latest.LipSuckUpper;

            shapes[(int)UnifiedExpressions.LipSuckLowerLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipSuckLowerRight].Weight = latest.LipSuckLower;
            
            // Lip raise/depress
            shapes[(int)UnifiedExpressions.MouthUpperUpLeft].Weight = latest.LipRaise_L;
            shapes[(int)UnifiedExpressions.MouthUpperUpRight].Weight = latest.LipRaise_R;

            shapes[(int)UnifiedExpressions.MouthUpperDeepenLeft].Weight = latest.LipDepress_L;
            shapes[(int)UnifiedExpressions.MouthUpperDeepenRight].Weight = latest.LipDepress_R;
            
            // Lip funnel/pucker
            shapes[(int)UnifiedExpressions.LipFunnelUpperLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipFunnelUpperRight].Weight = 
            shapes[(int)UnifiedExpressions.LipFunnelLowerLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipFunnelLowerRight].Weight = latest.MouthFunnel;

            shapes[(int)UnifiedExpressions.LipPuckerUpperRight].Weight = 
            shapes[(int)UnifiedExpressions.LipPuckerUpperLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipPuckerLowerLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipPuckerLowerRight].Weight = latest.MouthPucker;
            
            // Lip shift
            HandleLipShift(ref shapes);
            
            // Mouth roll
            shapes[(int)UnifiedExpressions.LipSuckUpperLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipSuckUpperRight].Weight = latest.MouthRoll_Up;
            shapes[(int)UnifiedExpressions.LipSuckLowerLeft].Weight = 
            shapes[(int)UnifiedExpressions.LipSuckLowerRight].Weight = latest.MouthRoll_Down;
            shapes[(int)UnifiedExpressions.MouthRaiserLower].Weight = latest.MouthShrugLower;
        }

        private void HandleLipShift(ref UnifiedExpressionShape[] shapes)
        {
            var latest = _latestData;
            if (latest == null) return;

            // Upper lip shift
            if(latest.LipShift_Up > 0)
            {
                shapes[(int)UnifiedExpressions.MouthUpperLeft].Weight = 0.0f;
                shapes[(int)UnifiedExpressions.MouthUpperRight].Weight = latest.LipShift_Up;
            }
            else
            {
                shapes[(int)UnifiedExpressions.MouthUpperLeft].Weight = -latest.LipShift_Up;
                shapes[(int)UnifiedExpressions.MouthUpperRight].Weight = 0.0f;
            }

            // Lower lip shift
            if (latest.LipShift_Down > 0)
            {
                shapes[(int)UnifiedExpressions.MouthLowerLeft].Weight = 0;
                shapes[(int)UnifiedExpressions.MouthLowerRight].Weight = latest.LipShift_Down;
            }
            else
            {
                shapes[(int)UnifiedExpressions.MouthLowerLeft].Weight = -latest.LipShift_Down;
                shapes[(int)UnifiedExpressions.MouthLowerRight].Weight = 0;
            }
        }

        private void UpdateMouthExpressions(ref UnifiedExpressionShape[] shapes)
        {
            var latest = _latestData;
            if (latest == null) return;

            // Jaw
            shapes[(int)UnifiedExpressions.JawOpen].Weight = latest.JawOpen;
            shapes[(int)UnifiedExpressions.JawForward].Weight = latest.JawFwd;
            
            // Jaw left/right
            if (latest.Jaw_Left_Right > 0)
            {
                shapes[(int)UnifiedExpressions.JawLeft].Weight = 0;
                shapes[(int)UnifiedExpressions.JawRight].Weight = latest.Jaw_Left_Right;
            }
            else
            {
                shapes[(int)UnifiedExpressions.JawLeft].Weight = -latest.Jaw_Left_Right;
                shapes[(int)UnifiedExpressions.JawRight].Weight = 0;
            }
            
            // Mouth left/right
            if (latest.Mouth_Left_Right > 0)
            {
                shapes[(int)UnifiedExpressions.MouthUpperLeft].Weight = shapes[(int)UnifiedExpressions.MouthLowerLeft].Weight = 0;
                shapes[(int)UnifiedExpressions.MouthUpperRight].Weight = shapes[(int)UnifiedExpressions.MouthLowerRight].Weight = latest.Mouth_Left_Right;
            }
            else
            {
                shapes[(int)UnifiedExpressions.MouthUpperLeft].Weight = shapes[(int)UnifiedExpressions.MouthLowerLeft].Weight = -latest.Mouth_Left_Right;
                shapes[(int)UnifiedExpressions.MouthUpperRight].Weight = shapes[(int)UnifiedExpressions.MouthLowerRight].Weight = 0;
            }
            
            // Other mouth expressions
            shapes[(int)UnifiedExpressions.MouthClosed].Weight = latest.MouthClose;
            shapes[(int)UnifiedExpressions.MouthCornerPullLeft].Weight = latest.MouthSmileLeft;
            shapes[(int)UnifiedExpressions.MouthCornerSlantLeft].Weight = latest.MouthSmileLeft;
            shapes[(int)UnifiedExpressions.MouthCornerPullRight].Weight = latest.MouthSmileRight;
            shapes[(int)UnifiedExpressions.MouthCornerSlantRight].Weight = latest.MouthSmileRight;
            shapes[(int)UnifiedExpressions.MouthFrownLeft].Weight = latest.MouthSadLeft;
            shapes[(int)UnifiedExpressions.MouthFrownRight].Weight = latest.MouthSadRight;
        }

        private void UpdateTongueExpressions(ref UnifiedExpressionShape[] shapes)
        {
            var latest = _latestData;
            if (latest == null) return;

            shapes[(int)UnifiedExpressions.TongueOut].Weight = latest.TongueOut;

            // Tongue left/right
            if (latest.Tongue_Left_Right > 0)
            {
                shapes[(int)UnifiedExpressions.TongueLeft].Weight = 0;
                shapes[(int)UnifiedExpressions.TongueRight].Weight = latest.Tongue_Left_Right;
            }
            else
            {
                shapes[(int)UnifiedExpressions.TongueLeft].Weight = -latest.Tongue_Left_Right;
                shapes[(int)UnifiedExpressions.TongueRight].Weight = 0;
            }

            // Tongue up/down
            if (latest.Tongue_Up_Down >= 0)
            {
                shapes[(int)UnifiedExpressions.TongueUp].Weight = latest.Tongue_Up_Down;
                shapes[(int)UnifiedExpressions.TongueDown].Weight = 0.0f;
            }
            else
            {
                shapes[(int)UnifiedExpressions.TongueDown].Weight = -latest.Tongue_Up_Down;
                shapes[(int)UnifiedExpressions.TongueUp].Weight = 0.0f;
            }

            // Add new tongue parameters if supported by VRCFaceTracking
            if (Enum.IsDefined(typeof(UnifiedExpressions), "TongueRoll"))
                shapes[(int)UnifiedExpressions.TongueRoll].Weight = latest.TongueRoll;

            if (Enum.IsDefined(typeof(UnifiedExpressions), "TongueWide"))
                shapes[(int)UnifiedExpressions.TongueFlat].Weight = latest.TongueWide;
        }

        // Optimized method to read blendshape values
        private void ReadBlendshapeValues(byte[] receiveBytes, ref CympleFaceDataStructs trackingData)
        {
            int offset = 12; // Start after header (prefix, flags, type, length)
            int expectedLength = offset + (Constants.blendShapeNames.Length * 4);
            
            // Check if we have enough data for all blendshapes
            if (receiveBytes.Length < expectedLength)
            {
                Logger.LogWarning($"Message too short: got {receiveBytes.Length} bytes, expected {expectedLength}");
                return;
            }
            
            // More efficient bulk reading
            for (int i = 0; i < Constants.blendShapeNames.Length; i++)
            {
                float value = BitConverter.ToSingle(receiveBytes, offset);
                SetBlendshapeValue(ref trackingData, i, value);
                offset += 4;
            }
        }

        // Handle socket exceptions separately for cleaner code
        private void HandleSocketException(SocketException se)
        {
            if (se.SocketErrorCode == SocketError.TimedOut)
            {
                if (!disconnectWarned)
                {
                    Logger.LogWarning("cympleFace connection lost");
                    disconnectWarned = true;
                    validateModule = (false, false);
                }
            }
            else
            {
                // some other network socket exception
                Logger.LogError(se.ToString());
            }
        }

        // Helper method to set blendshape values by index
        private void SetBlendshapeValue(ref CympleFaceDataStructs data, int index, float value)
        {
            switch (index)
            {
                case 0: data.EyePitch = value; break;
                case 1: data.EyeYaw_L = value; break;
                case 2: data.EyeYaw_R = value; break;
                case 3: data.Eye_Pupil_Left = value; break;
                case 4: data.Eye_Pupil_Right = value; break;
                case 5: data.EyeLidCloseLeft = value; break;
                case 6: data.EyeLidCloseRight = value; break;
                case 7: data.EyeSquintLeft = value; break;
                case 8: data.EyeSquintRight = value; break;
                case 9: data.CheekPuffLeft = value; break;
                case 10: data.CheekPuffRight = value; break;
                case 11: data.CheekSuck = value; break;
                case 12: data.Jaw_Left_Right = value; break;
                case 13: data.JawOpen = value; break;
                case 14: data.JawFwd = value; break;
                case 15: data.MouthClose = value; break;
                case 16: data.Mouth_Left_Right = value; break;
                case 17: data.LipSuckUpper = value; break;
                case 18: data.LipSuckLower = value; break;
                case 19: data.MouthFunnel = value; break;
                case 20: data.MouthPucker = value; break;
                case 21: data.LipRaise_L = value; break;
                case 22: data.LipRaise_R = value; break;
                case 23: data.LipDepress_L = value; break;
                case 24: data.LipDepress_R = value; break;
                case 25: data.LipShift_Up = value; break;
                case 26: data.LipShift_Down = value; break;
                case 27: data.MouthRoll_Up = value; break;
                case 28: data.MouthRoll_Down = value; break;
                case 29: data.MouthShrugLower = value; break;
                case 30: data.MouthSmileLeft = value; break;
                case 31: data.MouthSmileRight = value; break;
                case 32: data.MouthSadLeft = value; break;
                case 33: data.MouthSadRight = value; break;
                case 34: data.TongueOut = value; break;
                case 35: data.Tongue_Left_Right = value; break;
                case 36: data.Tongue_Up_Down = value; break;
                case 37: data.TongueWide = value; break;
                case 38: data.TongueRoll = value; break;
            }
        }

        // Called when the module is unloaded or VRCFaceTracking itself tears down.
        public override void Teardown()
        {
            // Set exit flag first
            Logger.LogInformation("Tearing down CympleFaceTracking module...");
            _isExiting = true;
            
            try
            {
                // Close UDP client to interrupt any blocking receive operations
                if (_CympleFaceConnection != null)
                {
                    _CympleFaceConnection.Close();
                    _CympleFaceConnection.Dispose();
                    _CympleFaceConnection = null;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during teardown: {ex.Message}");
            }
            _thread?.Join();
            Logger.LogInformation("CympleFaceTracking module teardown complete");
        }
    }
}
