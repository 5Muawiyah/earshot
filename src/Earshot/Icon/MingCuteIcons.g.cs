// Derived from MingCute Icons 3.1.0, https://github.com/mingcute-design/mingcute-icons, commit
// 9dc7d3ad074fb16477096a6a8109a6467d0e2fe3, licensed under the Apache License, Version 2.0 (licences\MingCute\LICENSE in
// the release, third_party\MingCute\LICENSE in the source).
// Changed by Earshot: the SVG path data of each file named below is converted to GDI+ path points (elliptical arcs to cubic
// Bezier curves per the SVG implementation notes, quadratic to cubic); the AirPods icon is also split into its left and right
// buds. No shape is otherwise changed. The source files and their hashes are listed in third_party\MingCute\manifest.json.
// Written by tests\Earshot.Tests\Icons\MingCuteIconGenerator.cs; edit the generator, not this file.

namespace Earshot.Icons;

internal static partial class MingCuteIcons
{
    // filled/communication/send
    internal static IconOutline FilledCommunicationSend => new("filled/communication/send", FilledCommunicationSendPoints, FilledCommunicationSendTypes);

    private static ReadOnlySpan<float> FilledCommunicationSendPoints =>
    [
        9.53f, 15.846f, 9.707f, 15.707f, 14.707f, 10.707f, 15.066f, 10.298f,
        15.112f, 9.698f, 14.707f, 9.293f, 14.303f, 8.888f, 13.703f, 8.935f,
        13.293f, 9.293f, 8.293f, 14.293f, 8.153f, 14.469f, 3.003f, 11.488f,
        2.482f, 11.148f, 2.231f, 10.532f, 2.471f, 9.934f, 2.608f, 9.592f,
        2.872f, 9.372f, 3.204f, 9.231f, 18.815f, 3.584f, 19.283f, 3.44f,
        19.769f, 3.521f, 20.124f, 3.876f, 20.479f, 4.231f, 20.559f, 4.717f,
        20.415f, 5.185f, 14.769f, 20.795f, 14.628f, 21.128f, 14.408f, 21.392f,
        14.065f, 21.529f, 13.468f, 21.769f, 12.852f, 21.518f, 12.512f, 20.997f,
    ];

    private static ReadOnlySpan<byte> FilledCommunicationSendTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // filled/design/layout_bottom
    internal static IconOutline FilledDesignLayoutBottom => new("filled/design/layout_bottom", FilledDesignLayoutBottomPoints, FilledDesignLayoutBottomTypes);

    private static ReadOnlySpan<float> FilledDesignLayoutBottomPoints =>
    [
        3f, 16f, 21f, 16f, 21f, 19f, 20.954f, 20.146f,
        20.146f, 20.954f, 19f, 21f, 5f, 21f, 3.854f, 20.954f,
        3.046f, 20.146f, 3f, 19f, 21f, 14f, 3f, 14f,
        3f, 5f, 3.046f, 3.854f, 3.854f, 3.046f, 5f, 3f,
        19f, 3f, 19.641f, 3.026f, 20.229f, 3.293f, 20.614f, 3.819f,
        20.871f, 4.17f, 20.983f, 4.57f, 21f, 5f,
    ];

    private static ReadOnlySpan<byte> FilledDesignLayoutBottomTypes =>
    [
        0, 1, 1, 3, 3, 3, 1, 3, 3, 131, 0, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // filled/devices/airpods_2
    internal static IconOutline FilledDevicesAirpods2 => new("filled/devices/airpods_2", FilledDevicesAirpods2Points, FilledDevicesAirpods2Types);

    private static ReadOnlySpan<float> FilledDevicesAirpods2Points =>
    [
        17f, 11f, 22f, 11f, 22f, 15f, 21.99f, 15.387f,
        21.976f, 15.628f, 21.898f, 16.008f, 21.413f, 18.385f, 19.386f, 19.94f,
        17f, 20f, 7f, 20f, 5.504f, 19.962f, 4.104f, 19.346f,
        3.142f, 18.18f, 2.393f, 17.273f, 2.029f, 16.167f, 2f, 15f,
        2f, 11f, 7f, 11f, 7.094f, 12.167f, 7.795f, 12.951f,
        9f, 13f, 15f, 13f, 16.205f, 12.951f, 16.906f, 12.167f,
        17f, 11f, 22f, 9f, 2f, 9f, 2.026f, 8.488f,
        2.072f, 8.006f, 2.225f, 7.513f, 2.887f, 5.383f, 4.804f, 4.055f,
        7f, 4f, 17f, 4f, 19.196f, 4.055f, 21.113f, 5.383f,
        21.775f, 7.513f, 21.928f, 8.006f, 21.974f, 8.488f, 22f, 9f,
    ];

    private static ReadOnlySpan<byte> FilledDevicesAirpods2Types =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3,
        3, 0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
    ];

    // filled/devices/cellphone
    internal static IconOutline FilledDevicesCellphone => new("filled/devices/cellphone", FilledDevicesCellphonePoints, FilledDevicesCellphoneTypes);

    private static ReadOnlySpan<float> FilledDevicesCellphonePoints =>
    [
        17.324f, 2.026f, 18.308f, 2.186f, 18.961f, 3.031f, 19f, 4f,
        19f, 20f, 18.954f, 21.146f, 18.146f, 21.954f, 17f, 22f,
        7f, 22f, 6.6807f, 21.9965f, 6.3664f, 21.9202f, 6.081f, 21.777f,
        5.403f, 21.426f, 5.03f, 20.748f, 5f, 20f, 5f, 4f,
        5.046f, 2.854f, 5.854f, 2.046f, 7f, 2f, 17f, 2f,
        12f, 16f, 10.571f, 16.155f, 10.77f, 18.004f, 12.01f, 18f,
        13.058f, 17.996f, 13.393f, 16.563f, 12.45f, 16.102f, 12.332f, 16.044f,
        12.133f, 15.986f, 12f, 16f,
    ];

    private static ReadOnlySpan<byte> FilledDevicesCellphoneTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 129, 0, 3, 3, 3,
        3, 3, 3, 3, 3, 3,
    ];

    // filled/devices/computer
    internal static IconOutline FilledDevicesComputer => new("filled/devices/computer", FilledDevicesComputerPoints, FilledDevicesComputerTypes);

    private static ReadOnlySpan<float> FilledDevicesComputerPoints =>
    [
        15f, 18f, 15f, 19f, 16f, 19f, 16.233f, 19.013f,
        16.441f, 19.071f, 16.625f, 19.22f, 17.385f, 19.829f, 16.923f, 20.947f,
        16f, 21f, 8f, 21f, 7.077f, 20.947f, 6.615f, 19.829f,
        7.375f, 19.22f, 7.5538f, 19.0805f, 7.7733f, 19.0032f, 8f, 19f,
        9f, 19f, 9f, 18f, 5f, 18f, 3.854f, 17.954f,
        3.046f, 17.146f, 3f, 16f, 3f, 5f, 3.026f, 4.359f,
        3.293f, 3.771f, 3.819f, 3.386f, 4.17f, 3.129f, 4.57f, 3.017f,
        5f, 3f, 19f, 3f, 19.245f, 3.01f, 19.399f, 3.024f,
        19.632f, 3.102f, 20.465f, 3.378f, 20.966f, 4.144f, 21f, 5f,
        21f, 16f, 20.954f, 17.146f, 20.146f, 17.954f, 19f, 18f,
        11f, 18f, 11f, 19f, 13f, 19f, 13f, 18f,
    ];

    private static ReadOnlySpan<byte> FilledDevicesComputerTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 1, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 131, 0, 1, 1, 129,
    ];

    // filled/devices/mouse
    internal static IconOutline FilledDevicesMouse => new("filled/devices/mouse", FilledDevicesMousePoints, FilledDevicesMouseTypes);

    private static ReadOnlySpan<float> FilledDevicesMousePoints =>
    [
        13.578f, 2.027f, 14.137f, 2.0807f, 14.6857f, 2.2127f, 15.208f, 2.419f,
        17.55f, 3.346f, 18.941f, 5.514f, 19f, 8f, 19f, 16f,
        18.931f, 18.888f, 17.022f, 21.338f, 14.14f, 21.892f, 13.7643f, 21.9643f,
        13.3826f, 22.0004f, 13f, 22f, 11f, 22f, 10.6174f, 22.0004f,
        10.2357f, 21.9643f, 9.86f, 21.892f, 6.978f, 21.338f, 5.069f, 18.888f,
        5f, 16f, 5f, 8f, 5.064f, 5.316f, 6.701f, 3.002f,
        9.316f, 2.24f, 9.8644f, 2.0866f, 10.4306f, 2.0059f, 11f, 2f,
        11.03f, 1.999f, 12.96f, 1.969f, 13.578f, 2.027f, 11.771f, 6.026f,
        11.305f, 6.135f, 11.026f, 6.54f, 11f, 7f, 11f, 11f,
        11.031f, 11.544f, 11.431f, 12f, 12f, 12f, 12.569f, 12f,
        12.969f, 11.544f, 13f, 11f, 13f, 7f, 12.964f, 6.37f,
        12.416f, 5.876f, 11.771f, 6.026f,
    ];

    private static ReadOnlySpan<byte> FilledDevicesMouseTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
    ];

    // filled/media/airpods
    internal static IconOutline FilledMediaAirpods => new("filled/media/airpods", FilledMediaAirpodsPoints, FilledMediaAirpodsTypes);

    private static ReadOnlySpan<float> FilledMediaAirpodsPoints =>
    [
        17f, 12.973f, 17f, 18f, 16.95f, 19.23f, 15.931f, 20.178f,
        14.676f, 19.974f, 14.3166f, 19.914f, 13.9804f, 19.7574f, 13.7031f, 19.521f,
        13.4259f, 19.2846f, 13.2181f, 18.9773f, 13.102f, 18.632f, 13.0343f, 18.4282f,
        12.9998f, 18.2148f, 13f, 18f, 13f, 8.5f, 13.072f, 5.813f,
        15.265f, 3.732f, 17.99f, 4.026f, 18.9298f, 4.129f, 19.8132f, 4.5255f,
        20.515f, 5.159f, 21.333f, 5.8994f, 21.8519f, 6.9134f, 21.974f, 8.01f,
        22.217f, 10.265f, 20.685f, 12.412f, 18.465f, 12.896f, 17.972f, 13.004f,
        17.501f, 13f, 17f, 12.973f, 7f, 12.973f, 6.338f, 13.009f,
        5.714f, 12.982f, 5.078f, 12.771f, 2.92f, 12.053f, 1.619f, 9.755f,
        2.104f, 7.535f, 2.3053f, 6.6184f, 2.7882f, 5.7876f, 3.485f, 5.159f,
        4.1868f, 4.5255f, 5.0702f, 4.129f, 6.01f, 4.026f, 8.735f, 3.732f,
        10.928f, 5.813f, 11f, 8.5f, 11f, 18f, 11f, 18.3145f,
        10.9258f, 18.6245f, 10.7835f, 18.9049f, 10.6412f, 19.1854f, 10.4348f, 19.4283f,
        10.181f, 19.614f, 8.825f, 20.608f, 7.065f, 19.624f, 7f, 18f,
    ];

    private static ReadOnlySpan<byte> FilledMediaAirpodsTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 3, 3, 131,
    ];

    // filled/media/airpods:left
    internal static IconOutline FilledMediaAirpodsLeft => new("filled/media/airpods:left", FilledMediaAirpodsLeftPoints, FilledMediaAirpodsLeftTypes);

    private static ReadOnlySpan<float> FilledMediaAirpodsLeftPoints =>
    [
        7f, 12.973f, 6.338f, 13.009f, 5.714f, 12.982f, 5.078f, 12.771f,
        2.92f, 12.053f, 1.619f, 9.755f, 2.104f, 7.535f, 2.3053f, 6.6184f,
        2.7882f, 5.7876f, 3.485f, 5.159f, 4.1868f, 4.5255f, 5.0702f, 4.129f,
        6.01f, 4.026f, 8.735f, 3.732f, 10.928f, 5.813f, 11f, 8.5f,
        11f, 18f, 11f, 18.3145f, 10.9258f, 18.6245f, 10.7835f, 18.9049f,
        10.6412f, 19.1854f, 10.4348f, 19.4283f, 10.181f, 19.614f, 8.825f, 20.608f,
        7.065f, 19.624f, 7f, 18f,
    ];

    private static ReadOnlySpan<byte> FilledMediaAirpodsLeftTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3,
        3, 131,
    ];

    // filled/media/airpods:right
    internal static IconOutline FilledMediaAirpodsRight => new("filled/media/airpods:right", FilledMediaAirpodsRightPoints, FilledMediaAirpodsRightTypes);

    private static ReadOnlySpan<float> FilledMediaAirpodsRightPoints =>
    [
        17f, 12.973f, 17f, 18f, 16.95f, 19.23f, 15.931f, 20.178f,
        14.676f, 19.974f, 14.3166f, 19.914f, 13.9804f, 19.7574f, 13.7031f, 19.521f,
        13.4259f, 19.2846f, 13.2181f, 18.9773f, 13.102f, 18.632f, 13.0343f, 18.4282f,
        12.9998f, 18.2148f, 13f, 18f, 13f, 8.5f, 13.072f, 5.813f,
        15.265f, 3.732f, 17.99f, 4.026f, 18.9298f, 4.129f, 19.8132f, 4.5255f,
        20.515f, 5.159f, 21.333f, 5.8994f, 21.8519f, 6.9134f, 21.974f, 8.01f,
        22.217f, 10.265f, 20.685f, 12.412f, 18.465f, 12.896f, 17.972f, 13.004f,
        17.501f, 13f, 17f, 12.973f,
    ];

    private static ReadOnlySpan<byte> FilledMediaAirpodsRightTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3,
    ];

    // filled/media/announcement
    internal static IconOutline FilledMediaAnnouncement => new("filled/media/announcement", FilledMediaAnnouncementPoints, FilledMediaAnnouncementTypes);

    private static ReadOnlySpan<float> FilledMediaAnnouncementPoints =>
    [
        19f, 8.001f, 19.358f, 8.025f, 19.451f, 8.013f, 19.798f, 8.108f,
        20.4246f, 8.2811f, 20.9781f, 8.6531f, 21.375f, 9.168f, 22.571f, 10.716f,
        21.969f, 13.004f, 20.168f, 13.765f, 19.784f, 13.927f, 19.411f, 13.973f,
        19f, 14.001f, 19f, 18.001f, 18.945f, 19.263f, 17.643f, 19.914f,
        16.6f, 19.201f, 14.307f, 17.481f, 12.911f, 16.473f, 11.662f, 15.94f,
        10f, 15.523f, 10f, 18.291f, 9.9955f, 18.6529f, 9.9232f, 19.0107f,
        9.787f, 19.346f, 8.991f, 21.231f, 6.435f, 21.577f, 5.165f, 19.973f,
        4.9487f, 19.6923f, 4.7857f, 19.3744f, 4.684f, 19.035f, 3.114f, 13.539f,
        2.833f, 13.172f, 2.589f, 12.779f, 2.2797f, 12.2175f, 2.0878f, 11.599f,
        2.0249f, 10.9611f, 1.9619f, 10.3232f, 2.0294f, 9.6791f, 2.223f, 9.068f,
        2.4657f, 8.3217f, 2.8898f, 7.6473f, 3.4574f, 7.1054f, 4.025f, 6.5634f,
        4.7183f, 6.1709f, 5.475f, 5.963f, 5.836f, 5.866f, 6.067f, 5.839f,
        6.439f, 5.808f, 8.204f, 5.664f, 9.911f, 5.726f, 11.614f, 5.215f,
        12.1666f, 5.0489f, 12.7052f, 4.8396f, 13.225f, 4.589f, 13.992f, 4.181f,
        16.744f, 2.576f, 17.382f, 2.241f, 18.152f, 2.332f, 18.637f, 2.891f,
        18.881f, 3.172f, 18.984f, 3.506f, 19f, 3.871f, 8f, 15.28f,
        7.179f, 15.235f, 6.426f, 15.227f, 5.634f, 15.079f, 6.607f, 18.486f,
        6.6406f, 18.605f, 6.7051f, 18.713f, 6.794f, 18.799f, 7.262f, 19.257f,
        7.959f, 18.903f, 8f, 18.291f, 19f, 12.001f, 20.343f, 11.846f,
        20.343f, 10.156f, 19f, 10.001f,
    ];

    private static ReadOnlySpan<byte> FilledMediaAnnouncementTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 131, 0, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 131, 0, 3, 3, 131,
    ];

    // filled/media/headphone
    internal static IconOutline FilledMediaHeadphone => new("filled/media/headphone", FilledMediaHeadphonePoints, FilledMediaHeadphoneTypes);

    private static ReadOnlySpan<float> FilledMediaHeadphonePoints =>
    [
        19.497f, 14.679f, 19.558f, 14.665f, 19.896f, 14.126f, 19.961f, 13.126f,
        19.988f, 12.503f, 20.119f, 9.508f, 18.794f, 6.781f, 16.217f, 5.191f,
        11.638f, 2.367f, 5.532f, 4.797f, 4.232f, 10.036f, 3.9342f, 11.278f,
        3.9103f, 12.5699f, 4.162f, 13.822f, 4.222f, 14.116f, 4.283f, 14.412f,
        4.442f, 14.665f, 4.503f, 14.679f, 4.653f, 14.578f, 4.749f, 14.454f,
        4.885f, 14.33f, 5.2642f, 13.9767f, 5.7428f, 13.7484f, 6.256f, 13.676f,
        6.756f, 13.608f, 7.274f, 13.693f, 7.724f, 13.921f, 8.187f, 14.156f,
        8.542f, 14.548f, 8.879f, 14.933f, 9.721f, 15.896f, 10.519f, 17.217f,
        9.987f, 18.524f, 9.402f, 19.965f, 7.47f, 21.084f, 6.04f, 20.125f,
        5.764f, 19.94f, 5.401f, 19.567f, 5.199f, 19.362f, 4.6789f, 18.8405f,
        4.2007f, 18.2787f, 3.769f, 17.682f, 2.66f, 16.128f, 2.097f, 14.488f,
        2.014f, 12.593f, 1.961f, 11.4f, 2.077f, 10.214f, 2.424f, 9.069f,
        3.694f, 4.876f, 7.629f, 2f, 12f, 2f, 16.371f, 2f,
        20.306f, 4.876f, 21.576f, 9.069f, 21.923f, 10.214f, 22.039f, 11.4f,
        21.986f, 12.593f, 21.903f, 14.488f, 21.34f, 16.128f, 20.231f, 17.682f,
        19.804f, 18.282f, 19.318f, 18.838f, 18.801f, 19.362f, 18.599f, 19.567f,
        18.236f, 19.94f, 17.96f, 20.125f, 16.53f, 21.084f, 14.598f, 19.965f,
        14.013f, 18.524f, 13.481f, 17.217f, 14.279f, 15.896f, 15.121f, 14.933f,
        15.458f, 14.548f, 15.813f, 14.156f, 16.276f, 13.921f, 16.726f, 13.693f,
        17.244f, 13.608f, 17.744f, 13.676f, 18.261f, 13.746f, 18.731f, 13.979f,
        19.115f, 14.33f, 19.251f, 14.454f, 19.347f, 14.578f, 19.497f, 14.679f,
    ];

    private static ReadOnlySpan<byte> FilledMediaHeadphoneTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // filled/media/mic_off
    internal static IconOutline FilledMediaMicOff => new("filled/media/mic_off", FilledMediaMicOffPoints, FilledMediaMicOffTypes);

    private static ReadOnlySpan<float> FilledMediaMicOffPoints =>
    [
        14.185f, 17.589f, 15.691f, 19.095f, 14.715f, 19.55f, 14.062f, 19.754f,
        13f, 19.936f, 13f, 21f, 12.974f, 21.229f, 12.788f, 22.026f,
        11.73f, 22.262f, 11.22f, 21.625f, 11.0811f, 21.4459f, 11.004f, 21.2266f,
        11f, 21f, 11f, 19.936f, 10.415f, 19.838f, 10.266f, 19.831f,
        9.698f, 19.661f, 8.5611f, 19.3154f, 7.5148f, 18.7227f, 6.6338f, 17.9254f,
        5.7528f, 17.1281f, 5.0589f, 16.1458f, 4.602f, 15.049f, 4.4349f, 14.6436f,
        4.3015f, 14.2252f, 4.203f, 13.798f, 4.086f, 13.289f, 3.945f, 12.739f,
        4.342f, 12.316f, 4.835f, 11.79f, 5.74f, 11.962f, 6.002f, 12.635f,
        6.059f, 12.782f, 6.159f, 13.38f, 6.163f, 13.395f, 6.327f, 14.087f,
        6.617f, 14.748f, 7.014f, 15.339f, 7.6203f, 16.2347f, 8.4555f, 16.9515f,
        9.4329f, 17.4147f, 10.4102f, 17.878f, 11.4939f, 18.0708f, 12.571f, 17.973f,
        13.136f, 17.92f, 13.651f, 17.77f, 14.185f, 17.589f, 16.17f, 14.756f,
        16.89f, 15.476f, 17.2f, 14.994f, 17.476f, 14.522f, 17.666f, 13.978f,
        17.829f, 13.429f, 17.93f, 13.018f, 17.916f, 12.579f, 18.245f, 12.27f,
        18.945f, 11.614f, 20.018f, 12.217f, 19.919f, 13.142f, 19.788f, 13.834f,
        19.52f, 14.975f, 18.992f, 15.957f, 18.317f, 16.903f, 20.485f, 19.071f,
        20.618f, 19.223f, 20.694f, 19.327f, 20.746f, 19.525f, 20.795f, 19.7227f,
        20.7831f, 19.9307f, 20.7118f, 20.1215f, 20.6405f, 20.3123f, 20.5132f, 20.4772f,
        20.3466f, 20.5943f, 20.1799f, 20.7115f, 19.9817f, 20.7755f, 19.778f, 20.778f,
        19.5f, 20.778f, 19.276f, 20.665f, 19.071f, 20.485f, 3.515f, 4.929f,
        3.336f, 4.724f, 3.222f, 4.5f, 3.222f, 4.222f, 3.222f, 3.577f,
        3.852f, 3.091f, 4.475f, 3.254f, 4.673f, 3.306f, 4.777f, 3.382f,
        4.929f, 3.515f, 7.158f, 5.744f, 7.364f, 5.09f, 7.617f, 4.493f,
        8.04f, 3.946f, 9.994f, 1.417f, 13.826f, 1.356f, 15.858f, 3.82f,
        16.607f, 4.727f, 16.971f, 5.833f, 17f, 7f, 17f, 12f,
        16.974f, 13.047f, 16.703f, 13.862f, 16.17f, 14.756f, 13.397f, 16.801f,
        12.931f, 16.913f, 12.482f, 17.001f, 12f, 17f, 9.837f, 16.997f,
        7.868f, 15.556f, 7.225f, 13.487f, 6.914f, 12.487f, 7f, 11.436f,
        7f, 10.404f,
    ];

    private static ReadOnlySpan<byte> FilledMediaMicOffTypes =>
    [
        0, 1, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0,
        1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 1, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 131,
    ];

    // filled/media/notification
    internal static IconOutline FilledMediaNotification => new("filled/media/notification", FilledMediaNotificationPoints, FilledMediaNotificationTypes);

    private static ReadOnlySpan<float> FilledMediaNotificationPoints =>
    [
        9.175f, 19f, 14.826f, 19f, 14.631f, 19.46f, 14.401f, 19.864f,
        14.03f, 20.207f, 12.796f, 21.345f, 10.807f, 21.222f, 9.72f, 19.946f,
        9.475f, 19.657f, 9.322f, 19.345f, 9.175f, 19f, 12.604f, 2.026f,
        15.731f, 2.292f, 18.368f, 4.713f, 18.899f, 7.806f, 18.976f, 8.257f,
        18.99f, 8.543f, 19f, 9f, 19f, 12.528f, 19.027f, 12.758f,
        19.106f, 12.975f, 20.823f, 16.408f, 21.037f, 16.9f, 20.931f, 17.47f,
        20.48f, 17.794f, 20.2914f, 17.9253f, 20.0678f, 17.997f, 19.838f, 18f,
        4.162f, 18f, 4.0066f, 18.0005f, 3.8529f, 17.9682f, 3.711f, 17.905f,
        3.125f, 17.643f, 2.932f, 16.971f, 3.178f, 16.408f, 4.895f, 12.975f,
        4.973f, 12.758f, 5f, 12.528f, 5f, 9f, 5.01f, 8.543f,
        5.024f, 8.257f, 5.102f, 7.806f, 5.714f, 4.237f, 9.002f, 1.718f,
        12.604f, 2.026f,
    ];

    private static ReadOnlySpan<byte> FilledMediaNotificationTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 3, 3,
        3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3,
    ];

    // filled/media/pause
    internal static IconOutline FilledMediaPause => new("filled/media/pause", FilledMediaPausePoints, FilledMediaPauseTypes);

    private static ReadOnlySpan<float> FilledMediaPausePoints =>
    [
        9.229f, 3.026f, 9.695f, 3.135f, 9.974f, 3.54f, 10f, 4f,
        10f, 20f, 9.967f, 20.568f, 9.568f, 20.967f, 9f, 21f,
        7f, 21f, 6.771f, 20.974f, 6.305f, 20.865f, 6.026f, 20.46f,
        6f, 20f, 6f, 4f, 6.033f, 3.432f, 6.432f, 3.033f,
        7f, 3f, 9f, 3f, 17.229f, 3.026f, 17.695f, 3.135f,
        17.974f, 3.54f, 18f, 4f, 18f, 20f, 17.967f, 20.568f,
        17.568f, 20.967f, 17f, 21f, 15f, 21f, 14.771f, 20.974f,
        14.305f, 20.865f, 14.026f, 20.46f, 14f, 20f, 14f, 4f,
        14.033f, 3.432f, 14.432f, 3.033f, 15f, 3f, 17f, 3f,
    ];

    private static ReadOnlySpan<byte> FilledMediaPauseTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 129, 0, 3, 3, 3, 1, 3,
        3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 129,
    ];

    // filled/system/lock
    internal static IconOutline FilledSystemLock => new("filled/system/lock", FilledSystemLockPoints, FilledSystemLockTypes);

    private static ReadOnlySpan<float> FilledSystemLockPoints =>
    [
        18f, 8f, 18.093f, 8f, 18.998f, 7.973f, 19.324f, 8.026f,
        20.308f, 8.186f, 20.961f, 9.031f, 21f, 10f, 21f, 20f,
        20.954f, 21.146f, 20.146f, 21.954f, 19f, 22f, 5f, 22f,
        4.465f, 21.978f, 3.97f, 21.798f, 3.586f, 21.414f, 3.202f, 21.03f,
        3.022f, 20.535f, 3f, 20f, 3f, 10f, 3.039f, 9.031f,
        3.692f, 8.186f, 4.676f, 8.026f, 5.002f, 7.973f, 5.907f, 8f,
        6f, 8f, 6.045f, 7.046f, 6.209f, 6.152f, 6.645f, 5.291f,
        8.531f, 1.568f, 13.613f, 0.871f, 16.43f, 3.953f, 17.1758f, 4.7688f,
        17.6817f, 5.7748f, 17.892f, 6.86f, 17.988f, 7.36f, 17.976f, 7.491f,
        18f, 8f, 11f, 15.729f, 11f, 17f, 11.026f, 17.229f,
        11.212f, 18.026f, 12.27f, 18.262f, 12.78f, 17.625f, 12.9195f, 17.4462f,
        12.9968f, 17.2267f, 13f, 17f, 13f, 15.729f, 13.335f, 15.501f,
        13.611f, 15.253f, 13.794f, 14.882f, 14.447f, 13.563f, 13.469f, 12f,
        12f, 12f, 10.531f, 12f, 9.554f, 13.563f, 10.206f, 14.882f,
        10.389f, 15.253f, 10.665f, 15.501f, 11f, 15.729f, 8f, 8f,
        16f, 8f, 15.964f, 7.379f, 15.87f, 6.805f, 15.593f, 6.241f,
        14.473f, 3.954f, 11.487f, 3.283f, 9.498f, 4.879f, 8.8009f, 5.4376f,
        8.3109f, 6.2135f, 8.106f, 7.083f, 8.012f, 7.483f, 8.024f, 7.589f,
        8f, 8f,
    ];

    private static ReadOnlySpan<byte> FilledSystemLockTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3,
    ];

    // filled/system/power
    internal static IconOutline FilledSystemPower => new("filled/system/power", FilledSystemPowerPoints, FilledSystemPowerTypes);

    private static ReadOnlySpan<float> FilledSystemPowerPoints =>
    [
        17.256f, 3.026f, 17.693f, 3.109f, 18.058f, 3.412f, 18.398f, 3.674f,
        18.7813f, 3.9693f, 19.143f, 4.2893f, 19.483f, 4.634f, 20.6121f, 5.7892f,
        21.4617f, 7.1876f, 21.9666f, 8.722f, 22.4715f, 10.2564f, 22.6184f, 11.886f,
        22.396f, 13.486f, 21.341f, 20.94f, 12.913f, 24.898f, 6.499f, 20.945f,
        1.766f, 18.029f, 0.109f, 11.845f, 2.787f, 6.96f, 3.4882f, 5.6819f,
        4.4467f, 4.5631f, 5.602f, 3.674f, 6.034f, 3.342f, 6.464f, 2.996f,
        7.033f, 3f, 8.079f, 3.007f, 8.808f, 4.105f, 8.41f, 5.071f,
        8.205f, 5.57f, 7.768f, 5.786f, 7.363f, 6.105f, 7.045f, 6.3557f,
        6.7493f, 6.6297f, 6.476f, 6.927f, 5.7027f, 7.7744f, 5.1326f, 8.7868f,
        4.8091f, 9.8874f, 4.4856f, 10.9881f, 4.4172f, 12.148f, 4.609f, 13.279f,
        5.473f, 18.317f, 11.126f, 21.04f, 15.608f, 18.576f, 19.213f, 16.595f,
        20.561f, 12.015f, 18.582f, 8.402f, 18.1919f, 7.6923f, 17.6909f, 7.0496f,
        17.098f, 6.498f, 16.637f, 6.105f, 16.232f, 5.786f, 15.795f, 5.57f,
        15.59f, 5.071f, 15.145f, 3.991f, 16.107f, 2.808f, 17.256f, 3.026f,
        12.27f, 1.524f, 12.998f, 1.656f, 13.468f, 2.283f, 13.5f, 3f,
        13.5f, 13f, 13.463f, 13.816f, 12.845f, 14.5f, 12f, 14.5f,
        11.155f, 14.5f, 10.537f, 13.816f, 10.5f, 13f, 10.5f, 3f,
        10.542f, 2.067f, 11.317f, 1.352f, 12.27f, 1.524f,
    ];

    private static ReadOnlySpan<byte> FilledSystemPowerTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
    ];

    // filled/system/refresh_2
    internal static IconOutline FilledSystemRefresh2 => new("filled/system/refresh_2", FilledSystemRefresh2Points, FilledSystemRefresh2Types);

    private static ReadOnlySpan<float> FilledSystemRefresh2Points =>
    [
        5.322f, 15.416f, 5.731f, 16.14f, 6.178f, 16.804f, 6.779f, 17.387f,
        9.246f, 19.782f, 13.073f, 20.178f, 15.983f, 18.356f, 16.6697f, 17.9264f,
        17.2823f, 17.3885f, 17.797f, 16.763f, 18.166f, 16.274f, 18.462f, 15.848f,
        18.649f, 15.392f, 19.138f, 15.153f, 20.187f, 14.641f, 21.43f, 15.524f,
        21.286f, 16.684f, 21.229f, 17.138f, 20.934f, 17.539f, 20.688f, 17.901f,
        19.8347f, 19.1561f, 18.7194f, 20.2114f, 17.419f, 20.994f, 12.05f, 24.229f,
        5.043f, 22.053f, 2.443f, 16.352f, 1.9271f, 15.2205f, 1.6162f, 14.0063f,
        1.525f, 12.766f, 1.509f, 12.541f, 1.474f, 12.072f, 1.521f, 11.791f,
        1.64f, 11.08f, 2.251f, 10.54f, 2.964f, 10.479f, 3.337f, 10.447f,
        3.675f, 10.553f, 3.99f, 10.746f, 6.668f, 12.551f, 6.9f, 12.721f,
        7.094f, 12.911f, 7.224f, 13.172f, 7.646f, 14.02f, 7.169f, 15.134f,
        6.264f, 15.414f, 5.945f, 15.513f, 5.643f, 15.479f, 5.322f, 15.416f,
        18.677f, 8.585f, 18.269f, 7.861f, 17.821f, 7.197f, 17.221f, 6.614f,
        14.754f, 4.219f, 10.927f, 3.823f, 8.017f, 5.646f, 7.3303f, 6.0756f,
        6.7177f, 6.6135f, 6.203f, 7.239f, 5.833f, 7.728f, 5.538f, 8.155f,
        5.35f, 8.61f, 4.862f, 8.848f, 3.813f, 9.36f, 2.57f, 8.476f,
        2.715f, 7.316f, 2.771f, 6.863f, 3.067f, 6.462f, 3.313f, 6.1f,
        4.1663f, 4.8449f, 5.2816f, 3.7896f, 6.582f, 3.007f, 11.951f, -0.228f,
        18.957f, 1.948f, 21.556f, 7.65f, 22.071f, 8.779f, 22.384f, 9.998f,
        22.474f, 11.236f, 22.491f, 11.461f, 22.526f, 11.93f, 22.479f, 12.211f,
        22.4157f, 12.5579f, 22.24f, 12.8743f, 21.9791f, 13.1114f, 21.7181f, 13.3485f,
        21.3863f, 13.4932f, 21.035f, 13.523f, 20.662f, 13.554f, 20.325f, 13.448f,
        20.01f, 13.255f, 17.332f, 11.451f, 17.1f, 11.281f, 16.905f, 11.091f,
        16.776f, 10.83f, 16.354f, 9.982f, 16.831f, 8.868f, 17.736f, 8.588f,
        18.055f, 8.489f, 18.356f, 8.524f, 18.677f, 8.585f,
    ];

    private static ReadOnlySpan<byte> FilledSystemRefresh2Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3,
    ];

    // filled/transportation/rocket
    internal static IconOutline FilledTransportationRocket => new("filled/transportation/rocket", FilledTransportationRocketPoints, FilledTransportationRocketTypes);

    private static ReadOnlySpan<float> FilledTransportationRocketPoints =>
    [
        9.926f, 18.315f, 8.944f, 19.195f, 7.557f, 19.504f, 6.307f, 19.799f,
        5.635f, 19.957f, 4.556f, 20.367f, 4.083f, 19.603f, 3.769f, 19.097f,
        4.082f, 18.195f, 4.201f, 17.694f, 4.519f, 16.475f, 4.777f, 15.605f,
        5.072f, 14.757f, 5.684f, 14.074f, 4.204f, 13.625f, 4.017f, 13.575f,
        3.717f, 13.512f, 3.525f, 13.43f, 3.275f, 13.321f, 3.0537f, 13.1557f,
        2.8781f, 12.947f, 2.7026f, 12.7383f, 2.5776f, 12.4919f, 2.513f, 12.227f,
        2.423f, 11.847f, 2.478f, 11.482f, 2.624f, 11.125f, 3.496f, 9.221f,
        3.694f, 8.832f, 3.969f, 8.511f, 4.356f, 8.299f, 4.636f, 8.146f,
        4.952f, 8.062f, 5.27f, 8.055f, 5.641f, 8.047f, 6.707f, 8.24f,
        6.72f, 8.235f, 6.745f, 8.226f, 6.674f, 8.253f, 6.754f, 8.21f,
        7.362f, 7.442f, 7.9303f, 6.7591f, 8.557f, 6.1271f, 9.235f, 5.553f,
        11.616f, 3.55f, 14.663f, 2.488f, 17.777f, 2.78f, 18.515f, 2.85f,
        20.026f, 3.026f, 20.539f, 3.639f, 21.01f, 4.2f, 21.21f, 5.753f,
        21.27f, 6.45f, 21.535f, 9.512f, 20.486f, 12.499f, 18.487f, 14.82f,
        17.9017f, 15.4934f, 17.2575f, 16.1131f, 16.562f, 16.672f, 15.778f, 17.269f,
        15.731f, 17.352f, 15.762f, 17.277f, 15.752f, 17.303f, 15.88f, 18.392f,
        15.921f, 18.913f, 15.806f, 19.416f, 15.484f, 19.838f, 15.2931f, 20.0828f,
        15.0503f, 20.2823f, 14.773f, 20.422f, 12.909f, 21.334f, 12.464f, 21.529f,
        11.992f, 21.571f, 11.533f, 21.39f, 10.581f, 21.015f, 10.494f, 20.189f,
        10.243f, 19.32f, 6.243f, 17.758f, 7.453f, 17.408f, 8.049f, 17.205f,
        8.757f, 17.021f, 8.757f, 16.243f, 8.7562f, 15.9782f, 8.6507f, 15.7245f,
        8.4636f, 15.5371f, 8.2764f, 15.3498f, 8.0228f, 15.2441f, 7.758f, 15.243f,
        6.989f, 15.243f, 6.796f, 15.952f, 6.593f, 16.548f, 13.95f, 7.888f,
        13.604f, 7.918f, 13.268f, 8.037f, 12.983f, 8.235f, 11.314f, 9.393f,
        12.265f, 12.046f, 14.291f, 11.874f, 14.636f, 11.844f, 14.972f, 11.724f,
        15.257f, 11.527f, 16.926f, 10.368f, 15.975f, 7.715f, 13.95f, 7.888f,
    ];

    private static ReadOnlySpan<byte> FilledTransportationRocketTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 131, 0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 131, 0, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // filled/weather/lightning
    internal static IconOutline FilledWeatherLightning => new("filled/weather/lightning", FilledWeatherLightningPoints, FilledWeatherLightningTypes);

    private static ReadOnlySpan<float> FilledWeatherLightningPoints =>
    [
        15.618f, 8f, 18.976f, 8f, 19.15f, 8.01f, 19.26f, 8.023f,
        19.418f, 8.099f, 20.071f, 8.412f, 20.147f, 9.214f, 19.69f, 9.724f,
        7.737f, 21.677f, 6.934f, 22.4f, 5.777f, 21.685f, 6.065f, 20.644f,
        8.613f, 13f, 5.015f, 13f, 4.804f, 12.978f, 4.161f, 12.844f,
        3.868f, 12.164f, 4.092f, 11.58f, 8.084f, 2.6f, 8.1472f, 2.4568f,
        8.2431f, 2.3304f, 8.364f, 2.231f, 8.552f, 2.075f, 8.767f, 2.014f,
        9.007f, 2f, 16.984f, 2f, 17.759f, 2.041f, 18.198f, 2.75f,
        17.887f, 3.462f,
    ];

    private static ReadOnlySpan<byte> FilledWeatherLightningTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 131,
    ];

    // regular/arrow/arrow_left
    internal static IconOutline RegularArrowArrowLeft => new("regular/arrow/arrow_left", RegularArrowArrowLeftPoints, RegularArrowArrowLeftTypes);

    private static ReadOnlySpan<float> RegularArrowArrowLeftPoints =>
    [
        6.757f, 13f, 10.707f, 16.95f, 11.13f, 17.431f, 11.088f, 18.183f,
        10.5f, 18.523f, 10.093f, 18.758f, 9.633f, 18.662f, 9.293f, 18.364f,
        3.636f, 12.707f, 3.505f, 12.558f, 3.429f, 12.453f, 3.377f, 12.259f,
        3.281f, 11.9f, 3.398f, 11.564f, 3.636f, 11.293f, 9.293f, 5.636f,
        9.633f, 5.338f, 10.093f, 5.242f, 10.5f, 5.477f, 11.088f, 5.817f,
        11.13f, 6.569f, 10.707f, 7.05f, 6.757f, 11f, 20f, 11f,
        20.543f, 11.036f, 21f, 11.428f, 21f, 12f, 21f, 12.572f,
        20.543f, 12.964f, 20f, 13f,
    ];

    private static ReadOnlySpan<byte> RegularArrowArrowLeftTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1,
        3, 3, 3, 3, 3, 131,
    ];

    // regular/arrow/down
    internal static IconOutline RegularArrowDown => new("regular/arrow/down", RegularArrowDownPoints, RegularArrowDownTypes);

    private static ReadOnlySpan<float> RegularArrowDownPoints =>
    [
        12f, 13.586f, 16.95f, 8.636f, 17.431f, 8.214f, 18.183f, 8.255f,
        18.523f, 8.843f, 18.758f, 9.25f, 18.662f, 9.711f, 18.364f, 10.05f,
        12.707f, 15.707f, 12.503f, 15.886f, 12.278f, 16f, 12f, 16f,
        11.722f, 16f, 11.497f, 15.886f, 11.293f, 15.707f, 5.636f, 10.05f,
        5.457f, 9.846f, 5.343f, 9.621f, 5.343f, 9.343f, 5.343f, 8.416f,
        6.396f, 8.062f, 7.05f, 8.636f,
    ];

    private static ReadOnlySpan<byte> RegularArrowDownTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/arrow/left
    internal static IconOutline RegularArrowLeft => new("regular/arrow/left", RegularArrowLeftPoints, RegularArrowLeftTypes);

    private static ReadOnlySpan<float> RegularArrowLeftPoints =>
    [
        10.414f, 12f, 15.364f, 16.95f, 15.786f, 17.431f, 15.745f, 18.183f,
        15.157f, 18.523f, 14.75f, 18.758f, 14.289f, 18.662f, 13.95f, 18.364f,
        8.293f, 12.707f, 8.114f, 12.503f, 8f, 12.278f, 8f, 12f,
        8f, 11.722f, 8.114f, 11.497f, 8.293f, 11.293f, 13.95f, 5.636f,
        14.154f, 5.457f, 14.379f, 5.343f, 14.657f, 5.343f, 15.584f, 5.343f,
        15.938f, 6.396f, 15.364f, 7.05f,
    ];

    private static ReadOnlySpan<byte> RegularArrowLeftTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/arrow/right
    internal static IconOutline RegularArrowRight => new("regular/arrow/right", RegularArrowRightPoints, RegularArrowRightTypes);

    private static ReadOnlySpan<float> RegularArrowRightPoints =>
    [
        13.586f, 12f, 8.636f, 7.05f, 8.062f, 6.396f, 8.416f, 5.343f,
        9.343f, 5.343f, 9.621f, 5.343f, 9.846f, 5.457f, 10.05f, 5.636f,
        15.707f, 11.293f, 15.886f, 11.497f, 16f, 11.722f, 16f, 12f,
        16f, 12.278f, 15.886f, 12.503f, 15.707f, 12.707f, 10.05f, 18.364f,
        9.711f, 18.662f, 9.25f, 18.758f, 8.843f, 18.523f, 8.255f, 18.183f,
        8.214f, 17.431f, 8.636f, 16.95f,
    ];

    private static ReadOnlySpan<byte> RegularArrowRightTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/arrow/transfer
    internal static IconOutline RegularArrowTransfer => new("regular/arrow/transfer", RegularArrowTransferPoints, RegularArrowTransferTypes);

    private static ReadOnlySpan<float> RegularArrowTransferPoints =>
    [
        6.414f, 16f, 8.707f, 18.293f, 8.866f, 18.5f, 8.9659f, 18.6782f,
        9.0094f, 18.8826f, 8.9908f, 19.0861f, 8.9721f, 19.2895f, 8.8922f, 19.4826f,
        8.7615f, 19.6397f, 8.6309f, 19.7968f, 8.4557f, 19.9106f, 8.259f, 19.966f,
        7.9f, 20.062f, 7.564f, 19.945f, 7.293f, 19.707f, 3.293f, 15.707f,
        2.934f, 15.298f, 2.889f, 14.697f, 3.293f, 14.293f, 3.489f, 14.096f,
        3.729f, 14.018f, 4f, 14f, 20f, 14f, 20.543f, 14.036f,
        21f, 14.428f, 21f, 15f, 21f, 15.572f, 20.543f, 15.964f,
        20f, 16f, 17.586f, 8f, 15.293f, 5.707f, 15.134f, 5.5f,
        15.0341f, 5.3218f, 14.9906f, 5.1174f, 15.0092f, 4.9139f, 15.0279f, 4.7105f,
        15.1078f, 4.5174f, 15.2385f, 4.3603f, 15.3691f, 4.2032f, 15.5443f, 4.0894f,
        15.741f, 4.034f, 16.1f, 3.938f, 16.436f, 4.055f, 16.707f, 4.293f,
        20.707f, 8.293f, 21.066f, 8.702f, 21.111f, 9.303f, 20.707f, 9.707f,
        20.511f, 9.904f, 20.271f, 9.982f, 20f, 10f, 4f, 10f,
        3.457f, 9.964f, 3f, 9.572f, 3f, 9f, 3f, 8.428f,
        3.457f, 8.036f, 4f, 8f,
    ];

    private static ReadOnlySpan<byte> RegularArrowTransferTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 131, 0, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
        3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/arrow/up
    internal static IconOutline RegularArrowUp => new("regular/arrow/up", RegularArrowUpPoints, RegularArrowUpTypes);

    private static ReadOnlySpan<float> RegularArrowUpPoints =>
    [
        12f, 10.414f, 7.05f, 15.364f, 6.396f, 15.938f, 5.343f, 15.584f,
        5.343f, 14.657f, 5.343f, 14.379f, 5.457f, 14.154f, 5.636f, 13.95f,
        11.293f, 8.293f, 11.497f, 8.114f, 11.722f, 8f, 12f, 8f,
        12.278f, 8f, 12.503f, 8.114f, 12.707f, 8.293f, 18.364f, 13.95f,
        18.662f, 14.289f, 18.758f, 14.75f, 18.523f, 15.157f, 18.183f, 15.745f,
        17.431f, 15.786f, 16.95f, 15.364f,
    ];

    private static ReadOnlySpan<byte> RegularArrowUpTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/communication/send
    internal static IconOutline RegularCommunicationSend => new("regular/communication/send", RegularCommunicationSendPoints, RegularCommunicationSendTypes);

    private static ReadOnlySpan<float> RegularCommunicationSendPoints =>
    [
        19.49f, 3.538f, 20.247f, 3.699f, 20.637f, 4.465f, 20.416f, 5.185f,
        14.769f, 20.796f, 14.51f, 21.4f, 13.873f, 21.768f, 13.222f, 21.559f,
        12.9265f, 21.4612f, 12.675f, 21.2622f, 12.512f, 20.997f, 9.025f, 14.975f,
        3.003f, 11.488f, 2.097f, 10.89f, 2.207f, 9.659f, 3.204f, 9.231f,
        18.815f, 3.584f, 19.037f, 3.516f, 19.261f, 3.489f, 19.49f, 3.538f,
        5.399f, 10.564f, 9.594f, 12.992f, 13.293f, 9.293f, 13.702f, 8.934f,
        14.303f, 8.889f, 14.707f, 9.293f, 15.111f, 9.697f, 15.066f, 10.298f,
        14.707f, 10.707f, 11.008f, 14.407f, 13.436f, 18.601f, 17.991f, 6.009f,
    ];

    private static ReadOnlySpan<byte> RegularCommunicationSendTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 0, 1, 1, 3,
        3, 3, 3, 3, 3, 1, 1, 129,
    ];

    // regular/design/layout_bottom
    internal static IconOutline RegularDesignLayoutBottom => new("regular/design/layout_bottom", RegularDesignLayoutBottomPoints, RegularDesignLayoutBottomTypes);

    private static ReadOnlySpan<float> RegularDesignLayoutBottomPoints =>
    [
        21f, 19.057f, 20.937f, 20.157f, 20.157f, 20.937f, 19.057f, 21f,
        4.943f, 21f, 4.498f, 20.974f, 4.097f, 20.847f, 3.745f, 20.565f,
        3.275f, 20.188f, 3.034f, 19.65f, 3f, 19.057f, 3f, 4.943f,
        3.026f, 4.498f, 3.153f, 4.097f, 3.435f, 3.745f, 3.812f, 3.275f,
        4.35f, 3.034f, 4.943f, 3f, 19.057f, 3f, 19.65f, 3.034f,
        20.188f, 3.275f, 20.565f, 3.745f, 20.847f, 4.097f, 20.974f, 4.498f,
        21f, 4.943f, 5f, 16f, 5.006f, 18.994f, 18.994f, 18.994f,
        19f, 16f, 5.006f, 5.006f, 5f, 14f, 19f, 14f,
        18.994f, 5.006f,
    ];

    private static ReadOnlySpan<byte> RegularDesignLayoutBottomTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        131, 0, 1, 1, 129, 0, 1, 1, 129,
    ];

    // regular/devices/airpods_2
    internal static IconOutline RegularDevicesAirpods2 => new("regular/devices/airpods_2", RegularDevicesAirpods2Points, RegularDevicesAirpods2Types);

    private static ReadOnlySpan<float> RegularDevicesAirpods2Points =>
    [
        6.942f, 19.998f, 6.551f, 19.979f, 6.182f, 19.951f, 5.8f, 19.853f,
        3.53f, 19.273f, 2.055f, 17.321f, 2f, 15f, 2f, 9f,
        2.031f, 7.682f, 2.508f, 6.458f, 3.427f, 5.504f, 4.367f, 4.529f,
        5.66f, 4.032f, 7f, 4f, 17f, 4f, 18.318f, 4.031f,
        19.542f, 4.508f, 20.496f, 5.427f, 21.471f, 6.367f, 21.968f, 7.66f,
        22f, 9f, 22f, 15f, 21.934f, 17.814f, 19.814f, 19.934f,
        17f, 20f, 7f, 20f, 7f, 11f, 4f, 11f,
        4f, 14.971f, 4.05f, 16.707f, 5.293f, 17.95f, 7.029f, 18f,
        16.971f, 18f, 18.707f, 17.95f, 19.95f, 16.707f, 20f, 14.971f,
        20f, 11f, 17f, 11f, 16.916f, 12.14f, 16.173f, 12.957f,
        15f, 13f, 9f, 13f, 7.827f, 12.957f, 7.084f, 12.14f,
        7f, 11f, 4.002f, 9f, 19.998f, 9f, 19.981f, 8.692f,
        19.989f, 8.612f, 19.919f, 8.311f, 19.59f, 6.907f, 18.379f, 6.041f,
        16.971f, 6f, 7.029f, 6f, 5.621f, 6.041f, 4.41f, 6.907f,
        4.081f, 8.311f, 4.011f, 8.612f, 4.019f, 8.692f, 4.002f, 9f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesAirpods2Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3,
        3, 129, 0, 1, 1, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 0, 1, 3,
        3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
    ];

    // regular/devices/battery_1
    internal static IconOutline RegularDevicesBattery1 => new("regular/devices/battery_1", RegularDevicesBattery1Points, RegularDevicesBattery1Types);

    private static ReadOnlySpan<float> RegularDevicesBattery1Points =>
    [
        5.034f, 9.741f, 5.26f, 8.899f, 6.43f, 8.745f, 6.866f, 9.5f,
        6.967f, 9.675f, 6.987f, 9.802f, 7f, 10f, 7f, 14f,
        6.97f, 14.451f, 6.712f, 14.844f, 6.259f, 14.966f, 5.603f, 15.142f,
        5.042f, 14.639f, 5f, 14f, 5f, 10f, 19f, 15f,
        19f, 15.368f, 19.04f, 15.923f, 18.967f, 16.344f, 18.799f, 17.311f,
        17.96f, 17.965f, 17f, 18f, 4f, 18f, 3.04f, 17.965f,
        2.201f, 17.311f, 2.033f, 16.344f, 2f, 16f, 2f, 8f,
        2.024f, 7.355f, 2.307f, 6.756f, 2.838f, 6.376f, 3.1795f, 6.1383f,
        3.584f, 6.0074f, 4f, 6f, 17f, 6f, 17.96f, 6.035f,
        18.799f, 6.689f, 18.967f, 7.656f, 19.04f, 8.077f, 19f, 8.632f,
        19f, 9f, 20.14f, 9.084f, 20.957f, 9.827f, 21f, 11f,
        21f, 13f, 20.957f, 14.173f, 20.14f, 14.916f, 19f, 15f,
        4.006f, 8.006f, 4.006f, 15.994f, 16.994f, 15.994f, 16.994f, 8.006f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesBattery1Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
        0, 1, 1, 129,
    ];

    // regular/devices/bluetooth
    internal static IconOutline RegularDevicesBluetooth => new("regular/devices/bluetooth", RegularDevicesBluetoothPoints, RegularDevicesBluetoothTypes);

    private static ReadOnlySpan<float> RegularDevicesBluetoothPoints =>
    [
        10.333f, 12f, 5.4f, 8.3f, 4.987f, 7.945f, 4.857f, 7.357f,
        5.2f, 6.9f, 5.543f, 6.443f, 6.144f, 6.402f, 6.6f, 6.7f,
        11f, 10f, 11f, 3f, 11.04f, 2.424f, 11.483f, 1.949f,
        12.09f, 2.004f, 12.302f, 2.023f, 12.424f, 2.086f, 12.6f, 2.2f,
        18.6f, 6.7f, 19.132f, 7.157f, 19.132f, 7.843f, 18.6f, 8.3f,
        13.667f, 12f, 18.6f, 15.7f, 19.132f, 16.157f, 19.132f, 16.843f,
        18.6f, 17.3f, 12.6f, 21.8f, 12.424f, 21.914f, 12.302f, 21.977f,
        12.09f, 21.996f, 11.483f, 22.051f, 11.04f, 21.576f, 11f, 21f,
        11f, 14f, 6.6f, 17.3f, 6.144f, 17.598f, 5.543f, 17.557f,
        5.2f, 17.1f, 4.857f, 16.643f, 4.987f, 16.055f, 5.4f, 15.7f,
        13f, 19f, 16.333f, 16.5f, 13f, 14f, 13f, 10f,
        16.333f, 7.5f, 13f, 5f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesBluetoothTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 131, 0, 1, 129, 0, 1, 129,
    ];

    // regular/devices/bluetooth_off
    internal static IconOutline RegularDevicesBluetoothOff => new("regular/devices/bluetooth_off", RegularDevicesBluetoothOffPoints, RegularDevicesBluetoothOffTypes);

    private static ReadOnlySpan<float> RegularDevicesBluetoothOffPoints =>
    [
        9.4f, 12.7f, 10.6f, 14.3f, 6.6f, 17.3f, 6.146f, 17.601f,
        5.541f, 17.555f, 5.2f, 17.1f, 4.859f, 16.645f, 4.984f, 16.051f,
        5.4f, 15.7f, 11f, 9.586f, 11f, 3f, 11.035f, 2.383f,
        11.562f, 1.891f, 12.198f, 2.02f, 12.365f, 2.054f, 12.459f, 2.106f,
        12.6f, 2.2f, 18.6f, 6.7f, 19.132f, 7.15f, 19.132f, 7.85f,
        18.6f, 8.3f, 13.523f, 12.108f, 20.485f, 19.071f, 20.91f, 19.556f,
        20.866f, 20.309f, 20.272f, 20.648f, 19.866f, 20.88f, 19.409f, 20.78f,
        19.071f, 20.485f, 3.515f, 4.929f, 2.643f, 3.932f, 3.932f, 2.642f,
        4.929f, 3.515f, 13f, 10f, 16.333f, 7.5f, 13f, 5f,
        11f, 16f, 13f, 16f, 13f, 19f, 14.4f, 17.95f,
        15.6f, 19.55f, 12.6f, 21.8f, 12.407f, 21.913f, 11.73f, 22.216f,
        11.039f, 21.7f, 11f, 21f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesBluetoothOffTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 131, 0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3,
        3, 3, 3, 3, 3, 1, 3, 3, 131, 0, 1, 129, 0, 1, 1, 1, 1, 1, 1, 3, 3, 131,
    ];

    // regular/devices/bluetooth_searching
    internal static IconOutline RegularDevicesBluetoothSearching => new("regular/devices/bluetooth_searching", RegularDevicesBluetoothSearchingPoints, RegularDevicesBluetoothSearchingTypes);

    private static ReadOnlySpan<float> RegularDevicesBluetoothSearchingPoints =>
    [
        8.333f, 12f, 3.4f, 8.3f, 2.987f, 7.945f, 2.857f, 7.357f,
        3.2f, 6.9f, 3.543f, 6.443f, 4.144f, 6.402f, 4.6f, 6.7f,
        9f, 10f, 9f, 3f, 9.04f, 2.424f, 9.483f, 1.949f,
        10.09f, 2.004f, 10.302f, 2.023f, 10.424f, 2.086f, 10.6f, 2.2f,
        16.6f, 6.7f, 17.132f, 7.157f, 17.132f, 7.843f, 16.6f, 8.3f,
        11.667f, 12f, 16.6f, 15.7f, 17.132f, 16.157f, 17.132f, 16.843f,
        16.6f, 17.3f, 10.6f, 21.8f, 10.424f, 21.914f, 10.302f, 21.977f,
        10.09f, 21.996f, 9.483f, 22.051f, 9.04f, 21.576f, 9f, 21f,
        9f, 14f, 4.6f, 17.3f, 4.144f, 17.598f, 3.543f, 17.557f,
        3.2f, 17.1f, 2.857f, 16.643f, 2.987f, 16.055f, 3.4f, 15.7f,
        11f, 19f, 14.333f, 16.5f, 11f, 14f, 11f, 10f,
        14.333f, 7.5f, 11f, 5f, 19.01f, 9.495f, 18.9915f, 9.3648f,
        18.9988f, 9.2323f, 19.0316f, 9.105f, 19.0644f, 8.9777f, 19.122f, 8.8581f,
        19.201f, 8.753f, 19.609f, 8.21f, 20.36f, 8.26f, 20.787f, 8.737f,
        22.135f, 10.244f, 22.339f, 12.508f, 21.446f, 14.287f, 21.193f, 14.789f,
        20.732f, 15.564f, 20.121f, 15.639f, 19.9468f, 15.6591f, 19.7704f, 15.6336f,
        19.6091f, 15.5649f, 19.4478f, 15.4961f, 19.3071f, 15.3866f, 19.201f, 15.247f,
        19.0952f, 15.1065f, 19.0282f, 14.9406f, 19.007f, 14.766f, 18.951f, 14.307f,
        19.226f, 14.053f, 19.467f, 13.705f, 19.6023f, 13.5097f, 19.713f, 13.301f,
        19.799f, 13.079f, 19.976f, 12.618f, 20.036f, 12.114f, 19.975f, 11.624f,
        19.9147f, 11.1467f, 19.7405f, 10.6909f, 19.467f, 10.295f, 19.28f, 10.026f,
        19.058f, 9.834f, 19.01f, 9.495f, 16.741f, 12.966f, 16.4849f, 12.8973f,
        16.2665f, 12.7297f, 16.134f, 12.5f, 15.695f, 11.74f, 16.424f, 10.81f,
        17.261f, 11.034f, 17.515f, 11.102f, 17.736f, 11.272f, 17.868f, 11.5f,
        18.307f, 12.26f, 17.578f, 13.19f, 16.741f, 12.966f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesBluetoothSearchingTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 131, 0, 1, 129, 0, 1, 129, 0, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3,
    ];

    // regular/devices/cellphone
    internal static IconOutline RegularDevicesCellphone => new("regular/devices/cellphone", RegularDevicesCellphonePoints, RegularDevicesCellphoneTypes);

    private static ReadOnlySpan<float> RegularDevicesCellphonePoints =>
    [
        11.741f, 17.966f, 11.4849f, 17.8973f, 11.2665f, 17.7297f, 11.134f, 17.5f,
        10.692f, 16.735f, 11.431f, 15.809f, 12.269f, 16.034f, 12.523f, 16.102f,
        12.744f, 16.272f, 12.876f, 16.5f, 13.318f, 17.265f, 12.579f, 18.191f,
        11.741f, 17.966f, 19f, 20.057f, 18.937f, 21.157f, 18.157f, 21.937f,
        17.057f, 22f, 6.943f, 22f, 6.35f, 21.966f, 5.812f, 21.725f,
        5.435f, 21.255f, 5.153f, 20.903f, 5.026f, 20.502f, 5f, 20.057f,
        5f, 3.943f, 5.034f, 3.35f, 5.275f, 2.812f, 5.745f, 2.435f,
        6.097f, 2.153f, 6.498f, 2.026f, 6.943f, 2f, 17.057f, 2f,
        18.157f, 2.063f, 18.937f, 2.843f, 19f, 3.943f, 7.006f, 4.006f,
        7.006f, 19.994f, 16.994f, 19.994f, 16.994f, 4.006f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesCellphoneTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 131, 0, 1, 1, 129,
    ];

    // regular/devices/computer
    internal static IconOutline RegularDevicesComputer => new("regular/devices/computer", RegularDevicesComputerPoints, RegularDevicesComputerTypes);

    private static ReadOnlySpan<float> RegularDevicesComputerPoints =>
    [
        15f, 18f, 15f, 19f, 16f, 19f, 16.271f, 19.018f,
        16.511f, 19.096f, 16.707f, 19.293f, 17.362f, 19.948f, 16.869f, 20.943f,
        16f, 21f, 8f, 21f, 7.131f, 20.943f, 6.638f, 19.948f,
        7.293f, 19.293f, 7.489f, 19.096f, 7.729f, 19.018f, 8f, 19f,
        9f, 19f, 9f, 18f, 4.943f, 18f, 3.843f, 17.937f,
        3.063f, 17.157f, 3f, 16.057f, 3f, 4.943f, 3.034f, 4.35f,
        3.275f, 3.812f, 3.745f, 3.435f, 4.097f, 3.153f, 4.498f, 3.026f,
        4.943f, 3f, 19.057f, 3f, 19.458f, 3.046f, 20.358f, 3.256f,
        20.948f, 4.041f, 21f, 4.943f, 21f, 16.057f, 20.937f, 17.157f,
        20.157f, 17.937f, 19.057f, 18f, 11f, 18f, 11f, 19f,
        13f, 19f, 13f, 18f, 5.006f, 5.006f, 5.006f, 15.994f,
        18.994f, 15.994f, 18.994f, 5.006f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesComputerTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 1, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 131, 0, 1, 1, 129, 0, 1, 1, 129,
    ];

    // regular/devices/device
    internal static IconOutline RegularDevicesDevice => new("regular/devices/device", RegularDevicesDevicePoints, RegularDevicesDeviceTypes);

    private static ReadOnlySpan<float> RegularDevicesDevicePoints =>
    [
        14.273f, 20f, 3.943f, 20f, 2.843f, 19.937f, 2.063f, 19.157f,
        2f, 18.057f, 2f, 5.943f, 2.026f, 5.498f, 2.153f, 5.097f,
        2.435f, 4.745f, 2.812f, 4.275f, 3.35f, 4.034f, 3.943f, 4f,
        17.057f, 4f, 17.502f, 4.026f, 17.903f, 4.153f, 18.255f, 4.435f,
        18.725f, 4.812f, 18.966f, 5.35f, 19f, 5.943f, 19f, 8f,
        19.196f, 8f, 20.087f, 7.96f, 20.458f, 8.046f, 21.358f, 8.256f,
        21.948f, 9.041f, 22f, 9.943f, 22f, 19.057f, 21.974f, 19.502f,
        21.847f, 19.903f, 21.565f, 20.255f, 21.188f, 20.725f, 20.65f, 20.966f,
        20.057f, 21f, 15.943f, 21f, 15.189f, 20.957f, 14.673f, 20.63f,
        14.273f, 20f, 16.006f, 10.006f, 16.006f, 18.994f, 19.994f, 18.994f,
        19.994f, 10.006f, 17f, 8f, 16.994f, 6.006f, 4.006f, 6.006f,
        4.006f, 17.994f, 14f, 18f, 14f, 9.943f, 14.052f, 9.041f,
        14.642f, 8.256f, 15.542f, 8.046f, 15.913f, 7.96f, 16.804f, 8f,
        17f, 8f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesDeviceTypes =>
    [
        0, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 0, 1, 1, 129, 0, 1, 1, 1, 1, 1, 3,
        3, 3, 3, 3, 3,
    ];

    // regular/devices/keyboard
    internal static IconOutline RegularDevicesKeyboard => new("regular/devices/keyboard", RegularDevicesKeyboardPoints, RegularDevicesKeyboardTypes);

    private static ReadOnlySpan<float> RegularDevicesKeyboardPoints =>
    [
        6.741f, 15.966f, 5.643f, 15.672f, 5.796f, 14.079f, 7f, 14f,
        17f, 14f, 17.543f, 14.036f, 18f, 14.428f, 18f, 15f,
        18f, 15.572f, 17.543f, 15.964f, 17f, 16f, 7f, 16f,
        15.741f, 12.966f, 15.011f, 12.77f, 14.758f, 11.827f, 15.293f, 11.293f,
        15.489f, 11.096f, 15.729f, 11.018f, 16f, 11f, 17f, 11f,
        17.271f, 11.018f, 17.511f, 11.096f, 17.707f, 11.293f, 17.957f, 11.543f,
        18.058f, 11.917f, 17.966f, 12.259f, 17.844f, 12.712f, 17.451f, 12.97f,
        17f, 13f, 16f, 13f, 11.241f, 12.966f, 10.511f, 12.77f,
        10.258f, 11.827f, 10.793f, 11.293f, 10.989f, 11.096f, 11.229f, 11.018f,
        11.5f, 11f, 12.5f, 11f, 12.771f, 11.018f, 13.011f, 11.096f,
        13.207f, 11.293f, 13.457f, 11.543f, 13.558f, 11.917f, 13.466f, 12.259f,
        13.344f, 12.712f, 12.951f, 12.97f, 12.5f, 13f, 11.5f, 13f,
        6.741f, 12.966f, 6.011f, 12.77f, 5.758f, 11.827f, 6.293f, 11.293f,
        6.489f, 11.096f, 6.729f, 11.018f, 7f, 11f, 8f, 11f,
        8.271f, 11.018f, 8.511f, 11.096f, 8.707f, 11.293f, 8.957f, 11.543f,
        9.058f, 11.917f, 8.966f, 12.259f, 8.844f, 12.712f, 8.451f, 12.97f,
        8f, 13f, 7f, 13f, 15.741f, 9.966f, 15.011f, 9.77f,
        14.758f, 8.827f, 15.293f, 8.293f, 15.489f, 8.096f, 15.729f, 8.018f,
        16f, 8f, 17f, 8f, 17.271f, 8.018f, 17.511f, 8.096f,
        17.707f, 8.293f, 17.957f, 8.543f, 18.058f, 8.917f, 17.966f, 9.259f,
        17.844f, 9.712f, 17.451f, 9.97f, 17f, 10f, 16f, 10f,
        11.241f, 9.966f, 10.511f, 9.77f, 10.258f, 8.827f, 10.793f, 8.293f,
        10.989f, 8.096f, 11.229f, 8.018f, 11.5f, 8f, 12.5f, 8f,
        12.771f, 8.018f, 13.011f, 8.096f, 13.207f, 8.293f, 13.457f, 8.543f,
        13.558f, 8.917f, 13.466f, 9.259f, 13.344f, 9.712f, 12.951f, 9.97f,
        12.5f, 10f, 11.5f, 10f, 6.741f, 9.966f, 6.011f, 9.77f,
        5.758f, 8.827f, 6.293f, 8.293f, 6.489f, 8.096f, 6.729f, 8.018f,
        7f, 8f, 8f, 8f, 8.271f, 8.018f, 8.511f, 8.096f,
        8.707f, 8.293f, 8.957f, 8.543f, 9.058f, 8.917f, 8.966f, 9.259f,
        8.844f, 9.712f, 8.451f, 9.97f, 8f, 10f, 7f, 10f,
        3.886f, 19.993f, 2.798f, 19.894f, 2.04f, 19.092f, 2f, 18f,
        2f, 6f, 2.021f, 5.431f, 2.239f, 4.897f, 2.667f, 4.513f,
        3.039f, 4.18f, 3.506f, 4.018f, 4f, 4f, 20f, 4f,
        20.569f, 4.021f, 21.103f, 4.239f, 21.487f, 4.667f, 21.82f, 5.039f,
        21.982f, 5.506f, 22f, 6f, 22f, 18f, 21.958f, 19.135f,
        21.135f, 19.958f, 20f, 20f, 4f, 20f, 4.006f, 6.006f,
        4.006f, 17.994f, 19.994f, 17.994f, 19.994f, 6.006f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesKeyboardTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 129,
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 129,
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 129, 0,
        1, 1, 129,
    ];

    // regular/devices/monitor
    internal static IconOutline RegularDevicesMonitor => new("regular/devices/monitor", RegularDevicesMonitorPoints, RegularDevicesMonitorTypes);

    private static ReadOnlySpan<float> RegularDevicesMonitorPoints =>
    [
        13f, 17.183f, 13f, 18.538f, 14.091f, 18.609f, 15.178f, 18.779f,
        16.242f, 19.03f, 16f, 20f, 16.242f, 19.03f, 17.508f, 19.506f,
        17.098f, 21.146f, 15.758f, 20.97f, 13.174f, 20.368f, 10.826f, 20.368f,
        8.242f, 20.97f, 6.902f, 21.146f, 6.492f, 19.506f, 7.757f, 19.03f,
        8f, 20f, 7.758f, 19.03f, 8.8234f, 18.7775f, 9.9077f, 18.613f,
        11f, 18.538f, 11f, 17.183f, 9.805f, 17.207f, 9.668f, 17.198f,
        8.189f, 17.318f, 6.272f, 17.512f, 4.341f, 17.784f, 3.591f, 17.867f,
        2.862f, 17.618f, 2.398f, 17f, 2.132f, 16.645f, 2.018f, 16.239f,
        2f, 15.8f, 2f, 6.2f, 2.027f, 5.548f, 2.3f, 4.956f,
        2.839f, 4.57f, 3.287f, 4.249f, 3.802f, 4.157f, 4.341f, 4.216f,
        6.272f, 4.488f, 7.545f, 4.642f, 8.815f, 4.746f, 10.097f, 4.799f,
        12.644f, 4.902f, 15.197f, 4.794f, 17.728f, 4.488f, 19.659f, 4.216f,
        20.198f, 4.157f, 20.713f, 4.249f, 21.161f, 4.57f, 21.7f, 4.956f,
        21.973f, 5.548f, 22f, 6.2f, 22f, 15.8f, 21.978f, 16.346f,
        21.793f, 16.846f, 21.397f, 17.232f, 20.925f, 17.694f, 20.303f, 17.855f,
        19.659f, 17.784f, 17.728f, 17.512f, 15.811f, 17.318f, 14.332f, 17.198f,
        14.195f, 17.207f, 13f, 17.183f, 4f, 6.199f, 4f, 15.801f,
        4.608f, 15.761f, 5.207f, 15.63f, 5.812f, 15.554f, 7.588f, 15.362f,
        11.1179f, 15.044f, 14.6719f, 15.1084f, 18.188f, 15.554f, 18.793f, 15.63f,
        19.392f, 15.762f, 20f, 15.801f, 20f, 6.199f, 19.392f, 6.238f,
        18.793f, 6.37f, 18.188f, 6.446f, 14.6719f, 6.8915f, 11.1179f, 6.9559f,
        7.588f, 6.638f, 5.812f, 6.446f, 5.207f, 6.37f, 4.608f, 6.239f,
        4f, 6.199f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesMonitorTypes =>
    [
        0, 1, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3,
        3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1,
        3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 0, 1, 3, 3, 3, 1,
        3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
    ];

    // regular/devices/mouse
    internal static IconOutline RegularDevicesMouse => new("regular/devices/mouse", RegularDevicesMousePoints, RegularDevicesMouseTypes);

    private static ReadOnlySpan<float> RegularDevicesMousePoints =>
    [
        11.034f, 6.741f, 11.26f, 5.899f, 12.43f, 5.745f, 12.866f, 6.5f,
        12.967f, 6.675f, 12.987f, 6.802f, 13f, 7f, 13f, 11f,
        12.97f, 11.451f, 12.712f, 11.844f, 12.259f, 11.966f, 11.603f, 12.142f,
        11.042f, 11.639f, 11f, 11f, 11f, 7f, 10.95f, 21.999f,
        10.372f, 21.973f, 9.821f, 21.907f, 9.264f, 21.744f, 6.687f, 20.986f,
        5.056f, 18.642f, 5f, 16f, 5f, 8f, 5.056f, 5.358f,
        6.687f, 3.014f, 9.264f, 2.256f, 9.8287f, 2.0938f, 10.4125f, 2.0077f,
        11f, 2f, 11.374f, 1.992f, 12.937f, 1.952f, 13.662f, 2.037f,
        14.253f, 2.107f, 14.835f, 2.252f, 15.38f, 2.492f, 17.612f, 3.475f,
        18.949f, 5.58f, 19f, 8f, 19f, 16f, 18.939f, 18.863f,
        17.078f, 21.244f, 14.257f, 21.867f, 13.8434f, 21.9527f, 13.4224f, 21.9973f,
        13f, 22f, 12.317f, 22.015f, 11.632f, 22.029f, 10.95f, 21.999f,
        11.025f, 4f, 8.736f, 4.058f, 7.058f, 5.736f, 7f, 8.025f,
        7f, 15.975f, 7.048f, 17.886f, 8.281f, 19.527f, 10.193f, 19.917f,
        10.503f, 19.981f, 10.706f, 19.992f, 11.025f, 20f, 12.975f, 20f,
        13.294f, 19.992f, 13.497f, 19.981f, 13.807f, 19.917f, 15.719f, 19.527f,
        16.952f, 17.886f, 17f, 15.975f, 17f, 8.025f, 16.952f, 6.114f,
        15.719f, 4.473f, 13.807f, 4.083f, 13.5333f, 4.0263f, 13.2545f, 3.9985f,
        12.975f, 4f,
    ];

    private static ReadOnlySpan<byte> RegularDevicesMouseTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        131,
    ];

    // regular/files/copy
    internal static IconOutline RegularFilesCopy => new("regular/files/copy", RegularFilesCopyPoints, RegularFilesCopyTypes);

    private static ReadOnlySpan<float> RegularFilesCopyPoints =>
    [
        7.771f, 16.974f, 6.876f, 16.765f, 6.736f, 15.504f, 7.56f, 15.102f,
        7.697f, 15.0349f, 7.8475f, 15f, 8f, 15f, 10f, 15f,
        10.383f, 15.022f, 10.725f, 15.206f, 10.898f, 15.56f, 11.237f, 16.253f,
        10.729f, 16.958f, 10f, 17f, 8f, 17f, 7.771f, 12.974f,
        6.876f, 12.765f, 6.736f, 11.504f, 7.56f, 11.102f, 7.697f, 11.0349f,
        7.8475f, 11f, 8f, 11f, 12f, 11f, 12.46f, 11.026f,
        12.865f, 11.305f, 12.974f, 11.771f, 13.124f, 12.416f, 12.63f, 12.964f,
        12f, 13f, 8f, 13f, 17f, 18f, 17f, 20f,
        16.974f, 20.641f, 16.707f, 21.229f, 16.181f, 21.614f, 15.83f, 21.871f,
        15.43f, 21.983f, 15f, 22f, 5f, 22f, 4.465f, 21.978f,
        3.97f, 21.798f, 3.586f, 21.414f, 3.202f, 21.03f, 3.022f, 20.535f,
        3f, 20f, 3f, 8f, 3.022f, 7.465f, 3.202f, 6.97f,
        3.586f, 6.586f, 3.97f, 6.202f, 4.465f, 6.022f, 5f, 6f,
        7f, 6f, 7f, 4f, 7.026f, 3.359f, 7.293f, 2.771f,
        7.819f, 2.386f, 8.17f, 2.129f, 8.57f, 2.017f, 9f, 2f,
        19f, 2f, 19.535f, 2.022f, 20.03f, 2.202f, 20.414f, 2.586f,
        20.798f, 2.97f, 20.978f, 3.465f, 21f, 4f, 21f, 16f,
        20.978f, 16.535f, 20.798f, 17.03f, 20.414f, 17.414f, 20.03f, 17.798f,
        19.535f, 17.978f, 19f, 18f, 17f, 16f, 19f, 16f,
        19f, 4f, 9f, 4f, 9f, 6f, 15f, 6f,
        16.146f, 6.046f, 16.954f, 6.854f, 17f, 8f, 5f, 8f,
        5f, 20f, 15f, 20f, 15f, 8f,
    ];

    private static ReadOnlySpan<byte> RegularFilesCopyTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 129, 0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3,
        3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 131, 0, 1, 1, 1, 1, 1, 3, 3, 131, 0, 1, 1, 129,
    ];

    // regular/files/download_2
    internal static IconOutline RegularFilesDownload2 => new("regular/files/download_2", RegularFilesDownload2Points, RegularFilesDownload2Types);

    private static ReadOnlySpan<float> RegularFilesDownload2Points =>
    [
        5.006f, 19.994f, 18.994f, 19.994f, 19f, 19.943f, 19f, 16f,
        19.034f, 15.741f, 19.0894f, 15.5443f, 19.2032f, 15.3691f, 19.3603f, 15.2385f,
        19.5174f, 15.1078f, 19.7105f, 15.0279f, 19.9139f, 15.0092f, 20.1174f, 14.9906f,
        20.3218f, 15.0341f, 20.5f, 15.134f, 20.822f, 15.32f, 20.976f, 15.64f,
        21f, 16f, 21f, 20f, 20.973f, 20.721f, 20.615f, 21.384f,
        19.976f, 21.742f, 19.6765f, 21.9061f, 19.3415f, 21.9946f, 19f, 22f,
        5f, 22f, 4.8085f, 21.9953f, 4.6184f, 21.9668f, 4.434f, 21.915f,
        3.575f, 21.662f, 3.032f, 20.878f, 3f, 20f, 3f, 16f,
        3.03f, 15.549f, 3.288f, 15.156f, 3.741f, 15.034f, 4.397f, 14.858f,
        4.958f, 15.361f, 5f, 16f, 5f, 19.943f, 11f, 13.243f,
        11f, 3f, 11.036f, 2.457f, 11.428f, 2f, 12f, 2f,
        12.572f, 2f, 12.964f, 2.457f, 13f, 3f, 13f, 13.243f,
        15.535f, 10.707f, 15.743f, 10.548f, 15.9212f, 10.4481f, 16.1256f, 10.4046f,
        16.3291f, 10.4232f, 16.5325f, 10.4419f, 16.7256f, 10.5218f, 16.8827f, 10.6525f,
        17.0398f, 10.7831f, 17.1536f, 10.9583f, 17.209f, 11.155f, 17.305f, 11.515f,
        17.188f, 11.85f, 16.95f, 12.121f, 12.707f, 16.364f, 12.503f, 16.543f,
        12.278f, 16.657f, 12f, 16.657f, 11.722f, 16.657f, 11.497f, 16.543f,
        11.293f, 16.364f, 7.05f, 12.121f, 6.812f, 11.85f, 6.695f, 11.515f,
        6.791f, 11.155f, 6.8464f, 10.9583f, 6.9602f, 10.7831f, 7.1173f, 10.6525f,
        7.2744f, 10.5218f, 7.4675f, 10.4419f, 7.6709f, 10.4232f, 7.8744f, 10.4046f,
        8.0788f, 10.4481f, 8.257f, 10.548f, 8.464f, 10.707f,
    ];

    private static ReadOnlySpan<byte> RegularFilesDownload2Types =>
    [
        0, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 1, 3, 3, 3, 3, 3, 3, 1,
        1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 129,
    ];

    // regular/files/external_link
    internal static IconOutline RegularFilesExternalLink => new("regular/files/external_link", RegularFilesExternalLinkPoints, RegularFilesExternalLinkTypes);

    private static ReadOnlySpan<float> RegularFilesExternalLinkPoints =>
    [
        5.006f, 8.006f, 5.006f, 18.994f, 15.994f, 18.994f, 16f, 18.943f,
        16f, 13f, 16.042f, 12.361f, 16.603f, 11.858f, 17.259f, 12.034f,
        17.712f, 12.156f, 17.97f, 12.549f, 18f, 13f, 18f, 19f,
        17.971f, 19.799f, 17.526f, 20.524f, 16.776f, 20.84f, 16.529f, 20.944f,
        16.267f, 20.99f, 16f, 21f, 5f, 21f, 4.431f, 20.979f,
        3.897f, 20.761f, 3.513f, 20.333f, 3.18f, 19.961f, 3.018f, 19.494f,
        3f, 19f, 3f, 8f, 3.01f, 7.733f, 3.056f, 7.471f,
        3.16f, 7.224f, 3.476f, 6.474f, 4.201f, 6.029f, 5f, 6f,
        11f, 6f, 11.451f, 6.03f, 11.844f, 6.288f, 11.966f, 6.741f,
        12.142f, 7.397f, 11.639f, 7.958f, 11f, 8f, 5.057f, 8f,
        17.586f, 5f, 15f, 5f, 13.796f, 4.921f, 13.643f, 3.328f,
        14.741f, 3.034f, 15f, 3f, 20f, 3f, 20.271f, 3.018f,
        20.511f, 3.096f, 20.707f, 3.293f, 20.904f, 3.489f, 20.982f, 3.729f,
        21f, 4f, 21f, 9f, 20.966f, 9.259f, 20.672f, 10.357f,
        19.079f, 10.204f, 19f, 9f, 19f, 6.414f, 10.707f, 14.707f,
        10.298f, 15.066f, 9.697f, 15.111f, 9.293f, 14.707f, 8.889f, 14.303f,
        8.934f, 13.702f, 9.293f, 13.293f,
    ];

    private static ReadOnlySpan<byte> RegularFilesExternalLinkTypes =>
    [
        0, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 1, 3, 3, 3, 1, 1, 3,
        3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/media/airpods
    internal static IconOutline RegularMediaAirpods => new("regular/media/airpods", RegularMediaAirpodsPoints, RegularMediaAirpodsTypes);

    private static ReadOnlySpan<float> RegularMediaAirpodsPoints =>
    [
        18f, 12.971f, 18f, 17.5f, 17.919f, 19.999f, 14.753f, 20.953f,
        13.388f, 18.834f, 13.13f, 18.434f, 13.015f, 17.972f, 13f, 17.5f,
        13f, 8.5f, 13.057f, 6.224f, 14.679f, 4.262f, 16.999f, 4.029f,
        18.0626f, 3.9184f, 19.1316f, 4.1818f, 20.022f, 4.774f, 20.7618f, 5.2777f,
        21.3362f, 5.9888f, 21.673f, 6.818f, 21.899f, 7.363f, 22.012f, 7.973f,
        21.998f, 8.562f, 21.942f, 10.878f, 20.271f, 12.657f, 18f, 12.971f,
        17.185f, 6.021f, 15.884f, 6.184f, 15.039f, 7.259f, 15f, 8.531f,
        15f, 17.455f, 15.015f, 17.709f, 15.139f, 17.938f, 15.409f, 17.988f,
        15.757f, 18.052f, 15.982f, 17.773f, 16f, 17.455f, 16f, 11.965f,
        16.037f, 11.694f, 16.17f, 11.219f, 16.598f, 10.966f, 17.071f, 10.967f,
        17.584f, 10.968f, 17.377f, 11.033f, 17.815f, 10.979f, 18.715f, 10.866f,
        19.503f, 10.249f, 19.83f, 9.403f, 20.511f, 7.645f, 19.051f, 5.789f,
        17.185f, 6.021f, 6f, 12.971f, 3.729f, 12.657f, 2.058f, 10.878f,
        2.002f, 8.562f, 1.9894f, 7.9645f, 2.1f, 7.3708f, 2.327f, 6.818f,
        2.6638f, 5.9888f, 3.2382f, 5.2777f, 3.978f, 4.774f, 4.8684f, 4.1818f,
        5.9374f, 3.9184f, 7.001f, 4.029f, 9.321f, 4.262f, 10.943f, 6.224f,
        11f, 8.5f, 11f, 17.5f, 10.985f, 17.972f, 10.87f, 18.434f,
        10.612f, 18.834f, 9.247f, 20.953f, 6.081f, 19.999f, 6f, 17.5f,
        6.185f, 6.021f, 3.788f, 6.32f, 3.198f, 9.571f, 5.326f, 10.706f,
        5.593f, 10.848f, 5.885f, 10.941f, 6.185f, 10.979f, 6.623f, 11.033f,
        6.416f, 10.968f, 6.929f, 10.967f, 7.402f, 10.966f, 7.83f, 11.219f,
        7.963f, 11.694f, 8f, 11.965f, 8f, 17.455f, 8.018f, 17.773f,
        8.243f, 18.052f, 8.591f, 17.988f, 8.861f, 17.938f, 8.985f, 17.709f,
        9f, 17.455f, 9f, 8.531f, 8.953f, 7.016f, 7.739f, 5.828f,
        6.185f, 6.021f,
    ];

    private static ReadOnlySpan<byte> RegularMediaAirpodsTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3,
        3,
    ];

    // regular/media/airpods:left
    internal static IconOutline RegularMediaAirpodsLeft => new("regular/media/airpods:left", RegularMediaAirpodsLeftPoints, RegularMediaAirpodsLeftTypes);

    private static ReadOnlySpan<float> RegularMediaAirpodsLeftPoints =>
    [
        6f, 12.971f, 3.729f, 12.657f, 2.058f, 10.878f, 2.002f, 8.562f,
        1.9894f, 7.9645f, 2.1f, 7.3708f, 2.327f, 6.818f, 2.6638f, 5.9888f,
        3.2382f, 5.2777f, 3.978f, 4.774f, 4.8684f, 4.1818f, 5.9374f, 3.9184f,
        7.001f, 4.029f, 9.321f, 4.262f, 10.943f, 6.224f, 11f, 8.5f,
        11f, 17.5f, 10.985f, 17.972f, 10.87f, 18.434f, 10.612f, 18.834f,
        9.247f, 20.953f, 6.081f, 19.999f, 6f, 17.5f, 6.185f, 6.021f,
        3.788f, 6.32f, 3.198f, 9.571f, 5.326f, 10.706f, 5.593f, 10.848f,
        5.885f, 10.941f, 6.185f, 10.979f, 6.623f, 11.033f, 6.416f, 10.968f,
        6.929f, 10.967f, 7.402f, 10.966f, 7.83f, 11.219f, 7.963f, 11.694f,
        8f, 11.965f, 8f, 17.455f, 8.018f, 17.773f, 8.243f, 18.052f,
        8.591f, 17.988f, 8.861f, 17.938f, 8.985f, 17.709f, 9f, 17.455f,
        9f, 8.531f, 8.953f, 7.016f, 7.739f, 5.828f, 6.185f, 6.021f,
    ];

    private static ReadOnlySpan<byte> RegularMediaAirpodsLeftTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131, 0,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
    ];

    // regular/media/airpods:right
    internal static IconOutline RegularMediaAirpodsRight => new("regular/media/airpods:right", RegularMediaAirpodsRightPoints, RegularMediaAirpodsRightTypes);

    private static ReadOnlySpan<float> RegularMediaAirpodsRightPoints =>
    [
        18f, 12.971f, 18f, 17.5f, 17.919f, 19.999f, 14.753f, 20.953f,
        13.388f, 18.834f, 13.13f, 18.434f, 13.015f, 17.972f, 13f, 17.5f,
        13f, 8.5f, 13.057f, 6.224f, 14.679f, 4.262f, 16.999f, 4.029f,
        18.0626f, 3.9184f, 19.1316f, 4.1818f, 20.022f, 4.774f, 20.7618f, 5.2777f,
        21.3362f, 5.9888f, 21.673f, 6.818f, 21.899f, 7.363f, 22.012f, 7.973f,
        21.998f, 8.562f, 21.942f, 10.878f, 20.271f, 12.657f, 18f, 12.971f,
        17.185f, 6.021f, 15.884f, 6.184f, 15.039f, 7.259f, 15f, 8.531f,
        15f, 17.455f, 15.015f, 17.709f, 15.139f, 17.938f, 15.409f, 17.988f,
        15.757f, 18.052f, 15.982f, 17.773f, 16f, 17.455f, 16f, 11.965f,
        16.037f, 11.694f, 16.17f, 11.219f, 16.598f, 10.966f, 17.071f, 10.967f,
        17.584f, 10.968f, 17.377f, 11.033f, 17.815f, 10.979f, 18.715f, 10.866f,
        19.503f, 10.249f, 19.83f, 9.403f, 20.511f, 7.645f, 19.051f, 5.789f,
        17.185f, 6.021f,
    ];

    private static ReadOnlySpan<byte> RegularMediaAirpodsRightTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3,
    ];

    // regular/media/announcement
    internal static IconOutline RegularMediaAnnouncement => new("regular/media/announcement", RegularMediaAnnouncementPoints, RegularMediaAnnouncementTypes);

    private static ReadOnlySpan<float> RegularMediaAnnouncementPoints =>
    [
        19f, 7.997f, 19.495f, 8.037f, 19.932f, 8.1f, 20.38f, 8.332f,
        22.72f, 9.543f, 22.449f, 13.019f, 19.948f, 13.85f, 19.54f, 13.986f,
        19.427f, 13.969f, 19f, 14.003f, 19f, 18.071f, 18.915f, 19.266f,
        17.664f, 19.892f, 16.657f, 19.243f, 14.321f, 17.491f, 12.926f, 16.484f,
        11.666f, 15.932f, 10f, 15.525f, 10f, 18.33f, 9.968f, 18.728f,
        9.58f, 21.116f, 6.436f, 21.825f, 5.059f, 19.834f, 4.872f, 19.564f,
        4.797f, 19.38f, 4.695f, 19.074f, 3.114f, 13.54f, 3.032f, 13.376f,
        2.885f, 13.254f, 2.784f, 13.102f, 2.511f, 12.637f, 2.2167f, 12.0563f,
        2.0454f, 11.4212f, 2.0079f, 10.7712f, 1.9704f, 10.1213f, 2.0675f, 9.4707f,
        2.293f, 8.86f, 2.5817f, 8.0951f, 3.0634f, 7.4178f, 3.6912f, 6.8941f,
        4.3191f, 6.3703f, 5.0717f, 6.0179f, 5.876f, 5.871f, 6.409f, 5.809f,
        8.05f, 5.669f, 9.66f, 5.735f, 11.257f, 5.314f, 12.227f, 5.058f,
        13.133f, 4.661f, 14.007f, 4.172f, 16.811f, 2.536f, 17.822f, 2.046f,
        18.914f, 2.673f, 19f, 3.793f, 5.632f, 15.071f, 6.596f, 18.445f,
        6.6308f, 18.5693f, 6.6937f, 18.684f, 6.78f, 18.78f, 7.262f, 19.281f,
        7.974f, 18.884f, 8f, 18.25f, 8f, 15.279f, 7.232f, 15.237f,
        6.396f, 15.254f, 5.632f, 15.071f, 17f, 4.741f, 16.652f, 4.944f,
        14.961f, 5.961f, 14.225f, 6.326f, 12.889f, 6.988f, 11.473f, 7.382f,
        10f, 7.585f, 10f, 13.491f, 11.6807f, 13.7816f, 13.2856f, 14.4077f,
        14.719f, 15.332f, 15.38f, 15.756f, 16.499f, 16.624f, 17f, 17f,
        8f, 13.276f, 8f, 7.724f, 6.985f, 7.78f, 5.937f, 7.701f,
        5.085f, 8.338f, 3.545f, 9.49f, 3.681f, 11.864f, 5.343f, 12.833f,
        6.142f, 13.298f, 7.114f, 13.227f, 8f, 13.276f, 19f, 10.004f,
        19f, 11.996f, 19.2642f, 11.996f, 19.5175f, 11.8911f, 19.7043f, 11.7043f,
        19.8911f, 11.5175f, 19.996f, 11.2642f, 19.996f, 11f, 19.996f, 10.7358f,
        19.8911f, 10.4825f, 19.7043f, 10.2957f, 19.5175f, 10.1089f, 19.2642f, 10.004f,
        19f, 10.004f,
    ];

    private static ReadOnlySpan<byte> RegularMediaAnnouncementTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 131, 0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 0, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 131, 0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 1, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/media/headphone
    internal static IconOutline RegularMediaHeadphone => new("regular/media/headphone", RegularMediaHeadphonePoints, RegularMediaHeadphoneTypes);

    private static ReadOnlySpan<float> RegularMediaHeadphonePoints =>
    [
        19.566f, 14.752f, 19.961f, 13.661f, 20.065f, 12.475f, 19.975f, 11.321f,
        19.8651f, 9.9297f, 19.3886f, 8.5923f, 18.594f, 7.445f, 15.417f, 2.863f,
        8.583f, 2.863f, 5.406f, 7.445f, 4.6114f, 8.5923f, 4.1349f, 9.9297f,
        4.025f, 11.321f, 3.935f, 12.475f, 4.039f, 13.661f, 4.434f, 14.752f,
        4.662f, 14.538f, 4.988f, 14.203f, 5.273f, 14.025f, 5.5452f, 13.856f,
        5.8474f, 13.7409f, 6.163f, 13.686f, 6.962f, 13.546f, 7.728f, 13.81f,
        8.322f, 14.345f, 9.309f, 15.325f, 10.288f, 16.389f, 10.475f, 17.924f,
        9.63f, 19.137f, 9.174f, 19.791f, 8.485f, 20.358f, 7.679f, 20.481f,
        6.422f, 20.672f, 5.698f, 19.895f, 4.908f, 19.064f, 4.391f, 18.519f,
        3.886f, 17.944f, 3.484f, 17.307f, 2.864f, 16.226f, 2.892f, 16.199f,
        2.128f, 14.638f, 1.895f, 12.894f, 2.029f, 11.166f, 2.147f, 9.6678f,
        2.6046f, 8.2161f, 3.367f, 6.921f, 4.1197f, 5.6458f, 5.146f, 4.5534f,
        6.3718f, 3.7227f, 7.5977f, 2.8919f, 8.9926f, 2.3435f, 10.456f, 2.117f,
        12.3571f, 1.8176f, 14.3041f, 2.0737f, 16.0631f, 2.8545f, 17.8221f, 3.6352f,
        19.3181f, 4.9075f, 20.3713f, 6.5182f, 21.4246f, 8.1289f, 21.9901f, 10.0095f,
        22f, 11.934f, 22.014f, 13.421f, 21.763f, 14.86f, 21.108f, 16.199f,
        21.136f, 16.226f, 20.516f, 17.307f, 20.011f, 18.107f, 19.352f, 18.818f,
        18.682f, 19.481f, 18.436f, 19.726f, 18.146f, 20.029f, 17.836f, 20.206f,
        17.532f, 20.3757f, 17.1944f, 20.4767f, 16.8471f, 20.5017f, 16.4998f, 20.5268f,
        16.1512f, 20.4753f, 15.826f, 20.351f, 15.245f, 20.131f, 14.722f, 19.641f,
        14.37f, 19.137f, 13.525f, 17.924f, 13.712f, 16.389f, 14.691f, 15.325f,
        15.678f, 14.345f, 16.272f, 13.81f, 17.038f, 13.546f, 17.837f, 13.686f,
        18.153f, 13.741f, 18.456f, 13.856f, 18.727f, 14.025f, 19.012f, 14.203f,
        19.338f, 14.538f, 19.566f, 14.752f, 5.412f, 16.578f, 5.82f, 17.1f,
        6.254f, 17.593f, 6.725f, 18.06f, 7.054f, 18.386f, 7.26f, 18.694f,
        7.669f, 18.329f, 7.824f, 18.191f, 7.981f, 18.028f, 8.069f, 17.837f,
        8.258f, 17.425f, 8.13f, 17f, 7.836f, 16.681f, 6.983f, 15.833f,
        6.87f, 15.733f, 6.757f, 15.659f, 6.602f, 15.657f, 6.448f, 15.655f,
        6.332f, 15.725f, 6.217f, 15.822f, 18.588f, 16.578f, 17.783f, 15.822f,
        17.668f, 15.725f, 17.552f, 15.655f, 17.398f, 15.657f, 17.243f, 15.659f,
        17.13f, 15.733f, 17.017f, 15.833f, 16.164f, 16.681f, 15.87f, 17f,
        15.742f, 17.425f, 15.931f, 17.837f, 16.019f, 18.028f, 16.176f, 18.191f,
        16.331f, 18.329f, 16.74f, 18.694f, 16.946f, 18.386f, 17.275f, 18.06f,
        17.641f, 17.687f, 17.978f, 17.332f, 18.288f, 16.962f, 18.588f, 16.578f,
    ];

    private static ReadOnlySpan<byte> RegularMediaHeadphoneTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131, 0, 1, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
    ];

    // regular/media/mic_off
    internal static IconOutline RegularMediaMicOff => new("regular/media/mic_off", RegularMediaMicOffPoints, RegularMediaMicOffTypes);

    private static ReadOnlySpan<float> RegularMediaMicOffPoints =>
    [
        11f, 19.932f, 8.186f, 19.563f, 5.727f, 17.728f, 4.622f, 15.101f,
        4.4474f, 14.6852f, 4.3082f, 14.2553f, 4.206f, 13.816f, 4.08f, 13.271f,
        3.934f, 12.657f, 4.42f, 12.241f, 4.5768f, 12.1099f, 4.7697f, 12.0295f,
        4.9732f, 12.0104f, 5.1767f, 11.9912f, 5.3812f, 12.0343f, 5.5597f, 12.1339f,
        5.7383f, 12.2334f, 5.8824f, 12.3848f, 5.973f, 12.568f, 6.077f, 12.785f,
        6.112f, 13.174f, 6.156f, 13.364f, 6.292f, 13.853f, 6.6554f, 14.9636f,
        7.3331f, 15.9449f, 8.243f, 16.678f, 8.9252f, 17.226f, 9.7189f, 17.6182f,
        10.5687f, 17.827f, 11.4184f, 18.0359f, 12.3035f, 18.0564f, 13.162f, 17.887f,
        13.534f, 17.814f, 13.895f, 17.638f, 14.278f, 17.75f, 15.116f, 17.993f,
        15.246f, 19.165f, 14.482f, 19.586f, 14.239f, 19.681f, 13.729f, 19.843f,
        13.502f, 19.858f, 13f, 19.932f, 13f, 21f, 12.987f, 21.198f,
        12.967f, 21.325f, 12.866f, 21.5f, 12.43f, 22.255f, 11.26f, 22.101f,
        11.034f, 21.259f, 11f, 21f, 16.169f, 14.756f, 16.888f, 15.474f,
        17.218f, 14.998f, 17.488f, 14.496f, 17.676f, 13.946f, 17.832f, 13.412f,
        17.944f, 12.945f, 17.932f, 12.468f, 18.368f, 12.172f, 19.135f, 11.652f,
        20.019f, 12.324f, 19.911f, 13.188f, 19.742f, 14.547f, 19.136f, 15.822f,
        18.315f, 16.901f, 20.485f, 19.072f, 20.616f, 19.221f, 20.692f, 19.326f,
        20.744f, 19.52f, 20.812f, 19.775f, 20.776f, 20.051f, 20.644f, 20.279f,
        20.305f, 20.867f, 19.553f, 20.908f, 19.071f, 20.486f, 14.121f, 15.536f,
        7.293f, 8.708f, 3.515f, 4.93f, 3.016f, 4.361f, 3.185f, 3.465f,
        3.963f, 3.257f, 4.1327f, 3.2115f, 4.3113f, 3.2115f, 4.481f, 3.257f,
        4.675f, 3.309f, 4.78f, 3.385f, 4.929f, 3.516f, 7.162f, 5.748f,
        7.312f, 5.24f, 7.491f, 4.769f, 7.778f, 4.318f, 8.3398f, 3.4462f,
        9.1564f, 2.7685f, 10.1168f, 2.3771f, 11.0772f, 1.9856f, 12.1348f, 1.8994f,
        13.146f, 2.13f, 14.0509f, 2.3447f, 14.8786f, 2.8062f, 15.537f, 3.463f,
        16.479f, 4.406f, 16.962f, 5.65f, 17f, 6.971f, 17f, 12.029f,
        16.9824f, 12.9977f, 16.6946f, 13.9422f, 16.169f, 14.756f, 9f, 7.587f,
        14.705f, 13.292f, 14.89f, 12.856f, 14.986f, 12.446f, 15f, 11.971f,
        15f, 7.029f, 14.986f, 6.557f, 14.903f, 6.108f, 14.694f, 5.682f,
        13.854f, 3.968f, 11.616f, 3.464f, 10.125f, 4.66f, 9.5105f, 5.1525f,
        9.1144f, 5.8669f, 9.022f, 6.649f, 8.989f, 6.931f, 9f, 7.318f,
        9f, 7.587f, 11.418f, 16.968f, 10.494f, 16.8555f, 9.6191f, 16.4893f,
        8.8905f, 15.91f, 8.1618f, 15.3307f, 7.6079f, 14.5609f, 7.29f, 13.686f,
        7.1006f, 13.1641f, 7.0031f, 12.6132f, 7.002f, 12.058f, 7.004f, 11.697f,
        7.14f, 11.368f, 7.451f, 11.164f, 7.6735f, 11.021f, 7.9432f, 10.9709f,
        8.2021f, 11.0246f, 8.4611f, 11.0783f, 8.6887f, 11.2314f, 8.836f, 11.451f,
        9.023f, 11.736f, 8.984f, 12.027f, 9.022f, 12.351f, 9.086f, 12.907f,
        9.31f, 13.439f, 9.66f, 13.875f, 10.0785f, 14.3974f, 10.6596f, 14.7649f,
        11.311f, 14.919f, 11.812f, 15.036f, 12.366f, 14.908f, 12.747f, 15.335f,
        12.8808f, 15.4893f, 12.9646f, 15.6806f, 12.9875f, 15.8836f, 13.0103f, 16.0865f,
        12.9711f, 16.2917f, 12.8749f, 16.4719f, 12.7788f, 16.6521f, 12.6303f, 16.7989f,
        12.449f, 16.893f, 12.14f, 17.049f, 11.744f, 17.006f, 11.418f, 16.968f,
    ];

    private static ReadOnlySpan<byte> RegularMediaMicOffTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        3, 129, 0, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 0, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/media/notification
    internal static IconOutline RegularMediaNotification => new("regular/media/notification", RegularMediaNotificationPoints, RegularMediaNotificationTypes);

    private static ReadOnlySpan<float> RegularMediaNotificationPoints =>
    [
        8.128f, 18f, 4.162f, 18f, 3.432f, 17.969f, 2.899f, 17.286f,
        3.115f, 16.567f, 3.178f, 16.408f, 4.877f, 13.01f, 4.932f, 12.889f,
        4.965f, 12.81f, 4.985f, 12.682f, 5f, 12.489f, 5f, 9f,
        5.009f, 8.551f, 5.04f, 8.16f, 5.118f, 7.718f, 5.701f, 4.442f,
        8.723f, 1.954f, 12.047f, 2.001f, 15.366f, 2.048f, 18.295f, 4.417f,
        18.882f, 7.718f, 18.96f, 8.16f, 18.991f, 8.551f, 19f, 9f,
        19f, 12.489f, 19.015f, 12.682f, 19.035f, 12.81f, 19.068f, 12.889f,
        19.123f, 13.01f, 20.822f, 16.408f, 20.885f, 16.567f, 21.101f, 17.286f,
        20.569f, 17.969f, 19.838f, 18f, 15.872f, 18f, 14.724f, 21.988f,
        9.276f, 21.988f, 8.128f, 18f, 10.27f, 18f, 11.16f, 19.338f,
        12.84f, 19.338f, 13.73f, 18f, 5.618f, 16f, 18.382f, 16f,
        17.317f, 13.87f, 17.1708f, 13.5682f, 17.0729f, 13.2452f, 17.027f, 12.913f,
        17f, 12.528f, 17f, 9.024f, 16.996f, 8.5412f, 16.9284f, 8.0611f,
        16.799f, 7.596f, 15.596f, 3.47f, 10.043f, 2.709f, 7.761f, 6.348f,
        7.252f, 7.161f, 7.023f, 8.071f, 7f, 9.024f, 7f, 12.528f,
        6.973f, 12.913f, 6.9271f, 13.2452f, 6.8292f, 13.5682f, 6.683f, 13.87f,
    ];

    private static ReadOnlySpan<byte> RegularMediaNotificationTypes =>
    [
        0, 1, 3, 3, 3, 1, 1, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        1, 1, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 0, 3, 3, 131, 0, 1, 1, 3, 3, 3,
        1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 131,
    ];

    // regular/media/pause
    internal static IconOutline RegularMediaPause => new("regular/media/pause", RegularMediaPausePoints, RegularMediaPauseTypes);

    private static ReadOnlySpan<float> RegularMediaPausePoints =>
    [
        15.034f, 4.741f, 15.328f, 3.643f, 16.921f, 3.796f, 17f, 5f,
        17f, 19f, 16.964f, 19.543f, 16.572f, 20f, 16f, 20f,
        15.428f, 20f, 15.036f, 19.543f, 15f, 19f, 15f, 5f,
        7.034f, 4.741f, 7.328f, 3.643f, 8.921f, 3.796f, 9f, 5f,
        9f, 19f, 8.964f, 19.543f, 8.572f, 20f, 8f, 20f,
        7.428f, 20f, 7.036f, 19.543f, 7f, 19f, 7f, 5f,
    ];

    private static ReadOnlySpan<byte> RegularMediaPauseTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129,
    ];

    // regular/media/play_circle
    internal static IconOutline RegularMediaPlayCircle => new("regular/media/play_circle", RegularMediaPlayCirclePoints, RegularMediaPlayCircleTypes);

    private static ReadOnlySpan<float> RegularMediaPlayCirclePoints =>
    [
        10.36f, 7.59f, 10.844f, 7.75f, 11.316f, 8.005f, 11.771f, 8.23f,
        12.9486f, 8.8134f, 14.0879f, 9.471f, 15.182f, 10.199f, 15.58f, 10.465f,
        16.555f, 11.037f, 16.747f, 11.508f, 17.097f, 12.367f, 16.229f, 12.848f,
        15.653f, 13.251f, 14.3053f, 14.1893f, 12.8829f, 15.0155f, 11.4f, 15.721f,
        10.998f, 15.913f, 10.317f, 16.307f, 9.853f, 16.242f, 8.976f, 16.12f,
        8.969f, 15.249f, 8.906f, 14.576f, 8.7388f, 12.7819f, 8.7391f, 10.9761f,
        8.907f, 9.182f, 8.963f, 8.594f, 8.961f, 7.865f, 9.607f, 7.596f,
        9.858f, 7.492f, 10.107f, 7.507f, 10.36f, 7.59f, 10.848f, 13.766f,
        11.9707f, 13.2058f, 13.0584f, 12.578f, 14.105f, 11.886f, 13.0584f, 11.1937f,
        11.9707f, 10.5655f, 10.848f, 10.005f, 10.7719f, 11.2575f, 10.7719f, 12.5135f,
        10.848f, 13.766f, 12.742f, 2.028f, 14.492f, 2.147f, 16.188f, 2.759f,
        17.637f, 3.74f, 22.946f, 7.332f, 23.462f, 14.925f, 18.834f, 19.299f,
        17.9223f, 20.1607f, 16.8536f, 20.8393f, 15.686f, 21.298f, 14.2782f, 21.848f,
        12.7657f, 22.0782f, 11.258f, 21.972f, 9.508f, 21.853f, 7.812f, 21.241f,
        6.363f, 20.26f, 1.054f, 16.668f, 0.538f, 9.075f, 5.166f, 4.701f,
        6.0777f, 3.8393f, 7.1464f, 3.1607f, 8.314f, 2.702f, 9.7218f, 2.152f,
        11.2343f, 1.9218f, 12.742f, 2.028f, 11.374f, 4.025f, 9.7742f, 4.1506f,
        8.2492f, 4.7544f, 6.997f, 5.758f, 2.03f, 9.744f, 3.51f, 17.709f,
        9.585f, 19.629f, 10.8402f, 20.0254f, 12.1738f, 20.1063f, 13.4677f, 19.8644f,
        14.7616f, 19.6224f, 15.9758f, 19.0652f, 17.003f, 18.242f, 21.97f, 14.257f,
        20.49f, 6.291f, 14.415f, 4.371f, 13.4325f, 4.0635f, 12.4004f, 3.946f,
        11.374f, 4.025f,
    ];

    private static ReadOnlySpan<byte> RegularMediaPlayCircleTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/media/volume
    internal static IconOutline RegularMediaVolume => new("regular/media/volume", RegularMediaVolumePoints, RegularMediaVolumeTypes);

    private static ReadOnlySpan<float> RegularMediaVolumePoints =>
    [
        6.678f, 16.005f, 6.628f, 16f, 4f, 16f, 3.355f, 15.976f,
        2.756f, 15.693f, 2.376f, 15.162f, 2.1383f, 14.8205f, 2.0074f, 14.416f,
        2f, 14f, 2f, 10f, 2.021f, 9.431f, 2.239f, 8.897f,
        2.667f, 8.513f, 3.039f, 8.18f, 3.506f, 8.018f, 4f, 8f,
        6.628f, 8f, 6.763f, 7.971f, 6.595f, 8.025f, 6.722f, 7.97f,
        13.419f, 3.186f, 13.741f, 2.988f, 14.11f, 2.932f, 14.458f, 3.111f,
        14.805f, 3.29f, 14.974f, 3.623f, 15f, 4f, 15f, 20f,
        14.981f, 20.284f, 14.895f, 20.534f, 14.681f, 20.732f, 14.319f, 21.069f,
        13.822f, 21.062f, 13.419f, 20.814f, 6.722f, 16.03f, 4.006f, 10.006f,
        4.006f, 13.994f, 4.894f, 14.027f, 5.792f, 13.969f, 6.68f, 14f,
        7.0948f, 14.0123f, 7.4976f, 14.1416f, 7.842f, 14.373f, 13f, 18.057f,
        13f, 5.943f, 7.842f, 9.627f, 7.4976f, 9.8584f, 7.0948f, 9.9877f,
        6.68f, 10f, 5.792f, 10.031f, 4.894f, 9.973f, 4.006f, 10.006f,
        18.037f, 7.796f, 17.803f, 6.956f, 18.732f, 6.228f, 19.491f, 6.657f,
        19.843f, 6.855f, 20.321f, 7.43f, 20.565f, 7.755f, 21.4248f, 8.8891f,
        21.9201f, 10.2575f, 21.9854f, 11.6791f, 22.0508f, 13.1008f, 21.6831f, 14.5088f,
        20.931f, 15.717f, 20.65f, 16.173f, 19.95f, 17.085f, 19.491f, 17.343f,
        19.3382f, 17.4292f, 19.1654f, 17.4737f, 18.99f, 17.472f, 18.7858f, 17.4673f,
        18.5879f, 17.4007f, 18.4223f, 17.2812f, 18.2567f, 17.1617f, 18.1312f, 16.9947f,
        18.0624f, 16.8024f, 17.9936f, 16.6101f, 17.9848f, 16.4014f, 18.037f, 16.204f,
        18.16f, 15.761f, 18.673f, 15.423f, 18.956f, 15.057f, 19.431f, 14.4423f,
        19.7563f, 13.7253f, 19.906f, 12.963f, 20.15f, 11.705f, 19.9046f, 10.4013f,
        19.22f, 9.318f, 19.0555f, 9.06f, 18.8678f, 8.8177f, 18.659f, 8.594f,
        18.42f, 8.337f, 18.135f, 8.15f, 18.037f, 7.796f, 16.031f, 10.01f,
        15.9659f, 9.7531f, 16.0055f, 9.4808f, 16.141f, 9.253f, 16.488f, 8.669f,
        17.24f, 8.637f, 17.716f, 9.066f, 19.138f, 10.345f, 19.41f, 12.622f,
        18.337f, 14.203f, 18.08f, 14.581f, 17.733f, 15.068f, 17.271f, 15.198f,
        16.544f, 15.403f, 15.845f, 14.722f, 16.031f, 13.99f, 16.11f, 13.678f,
        16.325f, 13.525f, 16.523f, 13.292f, 16.7067f, 13.0753f, 16.8421f, 12.8219f,
        16.9202f, 12.5488f, 16.9983f, 12.2757f, 17.0173f, 11.9891f, 16.976f, 11.708f,
        16.9214f, 11.3386f, 16.7645f, 10.9918f, 16.523f, 10.707f, 16.325f, 10.474f,
        16.11f, 10.321f, 16.031f, 10.01f,
    ];

    private static ReadOnlySpan<byte> RegularMediaVolumeTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3,
        3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 1, 3, 3,
        3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3,
    ];

    // regular/system/add
    internal static IconOutline RegularSystemAdd => new("regular/system/add", RegularSystemAddPoints, RegularSystemAddTypes);

    private static ReadOnlySpan<float> RegularSystemAddPoints =>
    [
        13f, 13f, 13f, 19f, 12.964f, 19.543f, 12.572f, 20f,
        12f, 20f, 11.428f, 20f, 11.036f, 19.543f, 11f, 19f,
        11f, 13f, 5f, 13f, 4.457f, 12.964f, 4f, 12.572f,
        4f, 12f, 4f, 11.428f, 4.457f, 11.036f, 5f, 11f,
        11f, 11f, 11f, 5f, 11.036f, 4.457f, 11.428f, 4f,
        12f, 4f, 12.572f, 4f, 12.964f, 4.457f, 13f, 5f,
        13f, 11f, 19f, 11f, 19.543f, 11.036f, 20f, 11.428f,
        20f, 12f, 20f, 12.572f, 19.543f, 12.964f, 19f, 13f,
    ];

    private static ReadOnlySpan<byte> RegularSystemAddTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3,
        1, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/system/alert
    internal static IconOutline RegularSystemAlert => new("regular/system/alert", RegularSystemAlertPoints, RegularSystemAlertTypes);

    private static ReadOnlySpan<float> RegularSystemAlertPoints =>
    [
        11.741f, 16.966f, 11.4849f, 16.8973f, 11.2665f, 16.7297f, 11.134f, 16.5f,
        10.695f, 15.741f, 11.424f, 14.81f, 12.261f, 15.035f, 12.515f, 15.103f,
        12.736f, 15.272f, 12.868f, 15.501f, 13.307f, 16.26f, 12.578f, 17.191f,
        11.741f, 16.966f, 11.034f, 8.742f, 11.26f, 7.899f, 12.43f, 7.745f,
        12.866f, 8.5f, 12.967f, 8.675f, 12.987f, 8.802f, 13f, 9f,
        13f, 13.001f, 12.97f, 13.451f, 12.712f, 13.845f, 12.259f, 13.966f,
        11.603f, 14.142f, 11.042f, 13.64f, 11f, 13.001f, 11f, 9f,
        3.213f, 20.341f, 2.109f, 20.212f, 1.56f, 19.069f, 2.067f, 18.103f,
        10.701f, 3.148f, 11.115f, 2.5f, 11.941f, 2.195f, 12.652f, 2.552f,
        12.926f, 2.69f, 13.134f, 2.89f, 13.299f, 3.148f, 21.933f, 18.103f,
        22.196f, 18.604f, 22.198f, 19.179f, 21.893f, 19.663f, 21.617f, 20.1f,
        21.142f, 20.333f, 20.634f, 20.353f, 3.366f, 20.353f, 4.232f, 18.353f,
        19.768f, 18.353f, 12f, 4.898f,
    ];

    private static ReadOnlySpan<byte> RegularSystemAlertTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
        3, 3, 3, 129, 0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0,
        1, 129,
    ];

    // regular/system/check
    internal static IconOutline RegularSystemCheck => new("regular/system/check", RegularSystemCheckPoints, RegularSystemCheckTypes);

    private static ReadOnlySpan<float> RegularSystemCheckPoints =>
    [
        9.171f, 16.072f, 19.778f, 5.465f, 20.187f, 5.106f, 20.788f, 5.061f,
        21.192f, 5.465f, 21.596f, 5.87f, 21.551f, 6.47f, 21.192f, 6.88f,
        9.878f, 18.193f, 9.674f, 18.372f, 9.449f, 18.486f, 9.171f, 18.486f,
        8.893f, 18.486f, 8.668f, 18.372f, 8.464f, 18.193f, 2.807f, 12.536f,
        2.51f, 12.197f, 2.414f, 11.736f, 2.648f, 11.329f, 2.988f, 10.741f,
        3.74f, 10.7f, 4.222f, 11.122f,
    ];

    private static ReadOnlySpan<byte> RegularSystemCheckTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/system/check_circle
    internal static IconOutline RegularSystemCheckCircle => new("regular/system/check_circle", RegularSystemCheckCirclePoints, RegularSystemCheckCircleTypes);

    private static ReadOnlySpan<float> RegularSystemCheckCirclePoints =>
    [
        10.586f, 13.331f, 15.535f, 8.381f, 16.02f, 7.957f, 16.773f, 8.001f,
        17.112f, 8.594f, 17.344f, 9f, 17.245f, 9.458f, 16.95f, 9.795f,
        11.293f, 15.452f, 11.079f, 15.615f, 10.674f, 15.847f, 10.216f, 15.747f,
        9.879f, 15.452f, 7.05f, 12.624f, 6.755f, 12.286f, 6.656f, 11.828f,
        6.887f, 11.423f, 7.226f, 10.829f, 7.979f, 10.785f, 8.464f, 11.21f,
        21.892f, 13.478f, 21.6453f, 15.1264f, 20.9904f, 16.6871f, 19.987f, 18.018f,
        16.163f, 23.086f, 8.621f, 23.352f, 4.453f, 18.561f, 3.5242f, 17.494f,
        2.8323f, 16.2421f, 2.423f, 14.888f, 1.9694f, 13.383f, 1.877f, 11.7923f,
        2.1533f, 10.2449f, 2.4295f, 8.6975f, 3.0667f, 7.2371f, 4.013f, 5.982f,
        7.837f, 0.914f, 15.379f, 0.648f, 19.547f, 5.439f, 20.4758f, 6.506f,
        21.1677f, 7.7579f, 21.577f, 9.112f, 22.001f, 10.521f, 22.107f, 12.022f,
        21.892f, 13.478f, 4.105f, 13.298f, 4.3429f, 14.7428f, 4.9728f, 16.0945f,
        5.926f, 17.206f, 10.144f, 22.124f, 18.216f, 20.216f, 19.767f, 13.922f,
        20.073f, 12.6849f, 20.079f, 11.3926f, 19.7845f, 10.1528f, 19.49f, 8.9129f,
        18.9036f, 7.7614f, 18.074f, 6.794f, 13.856f, 1.876f, 5.784f, 3.784f,
        4.233f, 10.078f, 3.9751f, 11.1319f, 3.9316f, 12.2269f, 4.105f, 13.298f,
    ];

    private static ReadOnlySpan<byte> RegularSystemCheckCircleTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131, 0, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/close
    internal static IconOutline RegularSystemClose => new("regular/system/close", RegularSystemClosePoints, RegularSystemCloseTypes);

    private static ReadOnlySpan<float> RegularSystemClosePoints =>
    [
        10.586f, 12.001f, 4.929f, 6.344f, 4.57f, 5.934f, 4.525f, 5.334f,
        4.929f, 4.93f, 5.333f, 4.525f, 5.934f, 4.571f, 6.343f, 4.93f,
        12f, 10.586f, 17.657f, 4.93f, 18.066f, 4.571f, 18.667f, 4.526f,
        19.071f, 4.93f, 19.475f, 5.334f, 19.43f, 5.935f, 19.071f, 6.344f,
        13.414f, 12.001f, 19.071f, 17.657f, 19.43f, 18.067f, 19.475f, 18.668f,
        19.071f, 19.072f, 18.667f, 19.476f, 18.066f, 19.431f, 17.657f, 19.072f,
        12f, 13.415f, 6.343f, 19.072f, 5.934f, 19.431f, 5.333f, 19.476f,
        4.929f, 19.072f, 4.525f, 18.668f, 4.57f, 18.067f, 4.929f, 17.658f,
    ];

    private static ReadOnlySpan<byte> RegularSystemCloseTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3,
        1, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/system/exit
    internal static IconOutline RegularSystemExit => new("regular/system/exit", RegularSystemExitPoints, RegularSystemExitTypes);

    private static ReadOnlySpan<float> RegularSystemExitPoints =>
    [
        6.836f, 5.016f, 6.321f, 5.101f, 6.02f, 5.543f, 6f, 6.04f,
        6f, 17.96f, 6.009f, 18.19f, 6.057f, 18.4f, 6.195f, 18.588f,
        6.399f, 18.868f, 6.704f, 18.986f, 7.04f, 19f, 12f, 19f,
        12.259f, 19.034f, 13.237f, 19.296f, 13.237f, 20.704f, 12.259f, 20.966f,
        12f, 21f, 7f, 21f, 5.303f, 20.949f, 4.051f, 19.697f,
        4f, 18f, 4f, 6f, 4.02f, 5.339f, 4.212f, 4.692f,
        4.627f, 4.168f, 5.212f, 3.427f, 6.062f, 3.028f, 7f, 3f,
        12f, 3f, 12.451f, 3.03f, 12.844f, 3.288f, 12.966f, 3.741f,
        13.142f, 4.397f, 12.639f, 4.958f, 12f, 5f, 7.04f, 5f,
        17.414f, 13f, 12f, 13f, 11.457f, 12.964f, 11f, 12.572f,
        11f, 12f, 11f, 11.428f, 11.457f, 11.036f, 12f, 11f,
        17.414f, 11f, 16.293f, 9.879f, 16.162f, 9.729f, 16.086f, 9.625f,
        16.034f, 9.43f, 15.808f, 8.588f, 16.744f, 7.869f, 17.5f, 8.306f,
        17.707f, 8.464f, 20.535f, 11.293f, 20.715f, 11.497f, 20.828f, 11.722f,
        20.828f, 12f, 20.828f, 12.278f, 20.715f, 12.503f, 20.535f, 12.707f,
        17.707f, 15.536f, 17.5f, 15.694f, 16.744f, 16.131f, 15.808f, 15.412f,
        16.034f, 14.57f, 16.086f, 14.375f, 16.162f, 14.27f, 16.293f, 14.121f,
    ];

    private static ReadOnlySpan<byte> RegularSystemExitTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3,
        3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129, 0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3,
        3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/system/history
    internal static IconOutline RegularSystemHistory => new("regular/system/history", RegularSystemHistoryPoints, RegularSystemHistoryTypes);

    private static ReadOnlySpan<float> RegularSystemHistoryPoints =>
    [
        19.921f, 10.915f, 19.143f, 5.658f, 13.678f, 2.52f, 8.771f, 4.681f,
        7.6225f, 5.1873f, 6.6092f, 5.9567f, 5.813f, 6.927f, 4.8572f, 8.0966f,
        4.2497f, 9.5115f, 4.06f, 11.01f, 3.507f, 15.463f, 6.782f, 19.552f,
        11.259f, 19.966f, 13.0433f, 20.1281f, 14.8306f, 19.6903f, 16.338f, 18.722f,
        16.887f, 18.336f, 16.991f, 18.255f, 17.421f, 17.843f, 17.657f, 17.733f,
        18.341f, 17.413f, 19.142f, 17.972f, 19.077f, 18.725f, 19.03f, 19.267f,
        18.526f, 19.59f, 18.141f, 19.894f, 17.457f, 20.381f, 15.832f, 21.453f,
        13.892f, 21.995f, 11.952f, 22f, 6.868f, 22.014f, 2.656f, 18.115f,
        2.063f, 13.118f, 1.9086f, 11.7982f, 2.0243f, 10.4607f, 2.403f, 9.187f,
        3.903f, 4.194f, 8.852f, 1.195f, 13.969f, 2.194f, 15.0498f, 2.4058f,
        16.0866f, 2.8001f, 17.035f, 3.36f, 18.492f, 4.2142f, 19.7102f, 5.4218f,
        20.5771f, 6.8713f, 21.444f, 8.3207f, 21.9317f, 9.9653f, 21.995f, 11.653f,
        22.0208f, 12.3878f, 21.9655f, 13.1233f, 21.83f, 13.846f, 21.697f, 14.57f,
        21.499f, 15.346f, 20.592f, 15.333f, 20.284f, 15.329f, 19.998f, 15.175f,
        19.79f, 14.956f, 17.538f, 12.369f, 17.442f, 12.238f, 17.3042f, 12.0197f,
        17.2476f, 11.7599f, 17.2821f, 11.504f, 17.3166f, 11.2482f, 17.44f, 11.0126f,
        17.6306f, 10.8386f, 17.8213f, 10.6645f, 18.0671f, 10.5631f, 18.325f, 10.552f,
        18.646f, 10.583f, 13f, 11.585f, 15.707f, 14.293f, 16.066f, 14.702f,
        16.111f, 15.303f, 15.707f, 15.707f, 15.303f, 16.111f, 14.702f, 16.066f,
        14.293f, 15.707f, 11.293f, 12.707f, 11.134f, 12.5f, 11.033f, 12.325f,
        11.013f, 12.198f, 11f, 12f, 11f, 7f, 11.03f, 6.549f,
        11.288f, 6.155f, 11.741f, 6.034f, 12.397f, 5.858f, 12.958f, 6.361f,
        13f, 7f,
    ];

    private static ReadOnlySpan<byte> RegularSystemHistoryTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 129, 0, 1, 3,
        3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131,
    ];

    // regular/system/information
    internal static IconOutline RegularSystemInformation => new("regular/system/information", RegularSystemInformationPoints, RegularSystemInformationTypes);

    private static ReadOnlySpan<float> RegularSystemInformationPoints =>
    [
        11f, 12f, 10.534f, 11.939f, 10.166f, 11.75f, 10.034f, 11.259f,
        10f, 11.1321f, 9.9913f, 10.9998f, 10.0085f, 10.8696f, 10.0257f, 10.7393f,
        10.0683f, 10.6137f, 10.134f, 10.5f, 10.32f, 10.178f, 10.64f, 10.024f,
        11f, 10f, 12f, 10f, 12.571f, 10.037f, 12.963f, 10.429f,
        13f, 11f, 13f, 16.134f, 13.182f, 16.274f, 13.248f, 16.296f,
        13.366f, 16.5f, 13.4959f, 16.7303f, 13.5304f, 17.0023f, 13.462f, 17.2577f,
        13.3935f, 17.5132f, 13.2277f, 17.7315f, 13f, 17.866f, 12.708f, 18.035f,
        12.321f, 18.021f, 12f, 18f, 11.729f, 17.982f, 11.489f, 17.904f,
        11.293f, 17.707f, 11.096f, 17.511f, 11.018f, 17.271f, 11f, 17f,
        11.741f, 8.966f, 11.4849f, 8.8973f, 11.2665f, 8.7297f, 11.134f, 8.5f,
        10.695f, 7.74f, 11.424f, 6.81f, 12.261f, 7.034f, 12.515f, 7.102f,
        12.736f, 7.272f, 12.868f, 7.5f, 13.307f, 8.26f, 12.578f, 9.19f,
        11.741f, 8.966f, 11.258f, 21.972f, 9.508f, 21.853f, 7.812f, 21.241f,
        6.363f, 20.26f, 1.054f, 16.668f, 0.538f, 9.075f, 5.166f, 4.701f,
        6.0777f, 3.8393f, 7.1464f, 3.1607f, 8.314f, 2.702f, 9.7218f, 2.152f,
        11.2343f, 1.9218f, 12.742f, 2.028f, 14.492f, 2.147f, 16.188f, 2.759f,
        17.637f, 3.74f, 22.946f, 7.332f, 23.462f, 14.925f, 18.834f, 19.299f,
        17.9223f, 20.1607f, 16.8536f, 20.8393f, 15.686f, 21.298f, 14.2782f, 21.848f,
        12.7657f, 22.0782f, 11.258f, 21.972f, 11.374f, 4.025f, 9.7742f, 4.1506f,
        8.2492f, 4.7544f, 6.997f, 5.758f, 2.03f, 9.744f, 3.51f, 17.709f,
        9.585f, 19.629f, 10.8402f, 20.0254f, 12.1738f, 20.1063f, 13.4677f, 19.8644f,
        14.7616f, 19.6224f, 15.9758f, 19.0652f, 17.003f, 18.242f, 21.97f, 14.257f,
        20.49f, 6.291f, 14.415f, 4.371f, 13.4325f, 4.0635f, 12.4004f, 3.946f,
        11.374f, 4.025f,
    ];

    private static ReadOnlySpan<byte> RegularSystemInformationTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 131, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/lock
    internal static IconOutline RegularSystemLock => new("regular/system/lock", RegularSystemLockPoints, RegularSystemLockTypes);

    private static ReadOnlySpan<float> RegularSystemLockPoints =>
    [
        13f, 15.727f, 13f, 17f, 12.966f, 17.259f, 12.74f, 18.101f,
        11.57f, 18.255f, 11.134f, 17.5f, 11.033f, 17.325f, 11.013f, 17.198f,
        11f, 17f, 11f, 15.727f, 10.105f, 15.158f, 9.713f, 14.11f,
        10.198f, 13.118f, 10.3639f, 12.7813f, 10.6204f, 12.4976f, 10.9388f, 12.2989f,
        11.2572f, 12.1001f, 11.6247f, 11.9942f, 12f, 11.993f, 12.3752f, 11.9947f,
        12.7425f, 12.1008f, 13.0608f, 12.2995f, 13.3791f, 12.4982f, 13.6358f, 12.7816f,
        13.802f, 13.118f, 14.287f, 14.11f, 13.895f, 15.158f, 13f, 15.727f,
        6f, 8f, 6.026f, 7.386f, 6.085f, 6.806f, 6.269f, 6.216f,
        7.18f, 3.284f, 10.201f, 1.506f, 13.208f, 2.121f, 14.3579f, 2.3577f,
        15.4132f, 2.9265f, 16.243f, 3.757f, 17.432f, 4.945f, 17.93f, 6.347f,
        18f, 8f, 18.196f, 8f, 19.087f, 7.96f, 19.458f, 8.046f,
        20.358f, 8.256f, 20.948f, 9.041f, 21f, 9.943f, 21f, 20.057f,
        20.937f, 21.157f, 20.157f, 21.937f, 19.057f, 22f, 4.943f, 22f,
        4.498f, 21.974f, 4.097f, 21.847f, 3.745f, 21.565f, 3.275f, 21.188f,
        3.034f, 20.65f, 3f, 20.057f, 3f, 9.943f, 3.052f, 9.041f,
        3.642f, 8.256f, 4.542f, 8.046f, 4.913f, 7.96f, 5.804f, 8f,
        6f, 8f, 5.006f, 10.006f, 5.006f, 19.994f, 18.994f, 19.994f,
        18.994f, 10.006f, 8.001f, 8f, 15.999f, 8f, 15.981f, 7.639f,
        15.99f, 7.546f, 15.917f, 7.193f, 15.7593f, 6.4274f, 15.3807f, 5.7248f,
        14.828f, 5.172f, 12.946f, 3.291f, 9.752f, 3.758f, 8.483f, 6.094f,
        8.157f, 6.695f, 8.035f, 7.324f, 8.001f, 8f,
    ];

    private static ReadOnlySpan<byte> RegularSystemLockTypes =>
    [
        0, 1, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1,
        3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 0, 1, 1, 129, 0, 1, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/minimize
    internal static IconOutline RegularSystemMinimize => new("regular/system/minimize", RegularSystemMinimizePoints, RegularSystemMinimizeTypes);

    private static ReadOnlySpan<float> RegularSystemMinimizePoints =>
    [
        4.741f, 12.966f, 3.643f, 12.672f, 3.796f, 11.079f, 5f, 11f,
        19f, 11f, 19.543f, 11.036f, 20f, 11.428f, 20f, 12f,
        20f, 12.572f, 19.543f, 12.964f, 19f, 13f, 5f, 13f,
    ];

    private static ReadOnlySpan<byte> RegularSystemMinimizeTypes =>
    [
        0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129,
    ];

    // regular/system/more_1
    internal static IconOutline RegularSystemMore1 => new("regular/system/more_1", RegularSystemMore1Points, RegularSystemMore1Types);

    private static ReadOnlySpan<float> RegularSystemMore1Points =>
    [
        18.475f, 10.564f, 18.893f, 10.78f, 19.878f, 11.5f, 19.635f, 13.054f,
        18.475f, 13.436f, 18f, 13.513f, 17.525f, 13.436f, 17.107f, 13.22f,
        16.122f, 12.5f, 16.365f, 10.946f, 17.525f, 10.564f, 18f, 10.487f,
        12.475f, 10.564f, 12.893f, 10.78f, 13.878f, 11.5f, 13.635f, 13.054f,
        12.475f, 13.436f, 12f, 13.513f, 11.525f, 13.436f, 11.107f, 13.22f,
        10.122f, 12.5f, 10.365f, 10.946f, 11.525f, 10.564f, 12f, 10.487f,
        6.475f, 10.564f, 6.893f, 10.78f, 7.878f, 11.5f, 7.635f, 13.054f,
        6.475f, 13.436f, 6f, 13.513f, 5.525f, 13.436f, 5.107f, 13.22f,
        4.122f, 12.5f, 4.365f, 10.946f, 5.525f, 10.564f, 6f, 10.487f,
    ];

    private static ReadOnlySpan<byte> RegularSystemMore1Types =>
    [
        0, 1, 3, 3, 3, 1, 1, 1, 3, 3, 3, 129, 0, 1, 3, 3, 3, 1, 1, 1, 3, 3, 3, 129,
        0, 1, 3, 3, 3, 1, 1, 1, 3, 3, 3, 129,
    ];

    // regular/system/power
    internal static IconOutline RegularSystemPower => new("regular/system/power", RegularSystemPowerPoints, RegularSystemPowerTypes);

    private static ReadOnlySpan<float> RegularSystemPowerPoints =>
    [
        16.065f, 4.911f, 15.755f, 4.222f, 16.325f, 3.429f, 17.077f, 3.505f,
        17.475f, 3.545f, 17.849f, 3.879f, 18.136f, 4.102f, 19.9278f, 5.4991f,
        21.1938f, 7.4616f, 21.728f, 9.67f, 23.302f, 16.267f, 17.976f, 22.492f,
        11.22f, 21.971f, 9.7147f, 21.8536f, 8.2556f, 21.3968f, 6.952f, 20.635f,
        2.922f, 18.275f, 1.047f, 13.383f, 2.479f, 8.932f, 3.0203f, 7.26f,
        3.9909f, 5.7593f, 5.294f, 4.58f, 5.864f, 4.102f, 6.319f, 3.749f,
        6.829f, 3.315f, 7.434f, 3.588f, 7.6194f, 3.6742f, 7.7742f, 3.8147f,
        7.878f, 3.9908f, 7.9819f, 4.1669f, 8.0298f, 4.3704f, 8.0155f, 4.5743f,
        8.0012f, 4.7783f, 7.9254f, 4.9731f, 7.798f, 5.133f, 7.641f, 5.325f,
        7.224f, 5.58f, 7.093f, 5.682f, 6.637f, 6.065f, 5.5947f, 7.0081f,
        4.8179f, 8.208f, 4.384f, 9.545f, 2.961f, 13.966f, 5.642f, 18.728f,
        10.165f, 19.788f, 15.953f, 21.145f, 21.161f, 15.917f, 19.781f, 10.134f,
        19.3551f, 8.3672f, 18.3418f, 6.7975f, 16.907f, 5.682f, 16.608f, 5.449f,
        16.23f, 5.276f, 16.065f, 4.911f, 11.034f, 2.741f, 11.328f, 1.643f,
        12.921f, 1.796f, 13f, 3f, 13f, 13f, 12.964f, 13.543f,
        12.572f, 14f, 12f, 14f, 11.428f, 14f, 11.036f, 13.543f,
        11f, 13f, 11f, 3f,
    ];

    private static ReadOnlySpan<byte> RegularSystemPowerTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 129,
    ];

    // regular/system/refresh_1
    internal static IconOutline RegularSystemRefresh1 => new("regular/system/refresh_1", RegularSystemRefresh1Points, RegularSystemRefresh1Types);

    private static ReadOnlySpan<float> RegularSystemRefresh1Points =>
    [
        19.521f, 9.287f, 19.033f, 7.872f, 18.094f, 6.604f, 16.916f, 5.687f,
        15.9039f, 4.9013f, 14.7186f, 4.3688f, 13.459f, 4.134f, 8.189f, 3.149f,
        3.416f, 7.627f, 4.055f, 12.952f, 4.585f, 17.364f, 8.677f, 20.533f,
        13.086f, 19.927f, 14.928f, 19.669f, 16.6243f, 18.7827f, 17.888f, 17.418f,
        18.289f, 16.948f, 18.385f, 16.825f, 18.661f, 16.382f, 18.857f, 16.223f,
        19.444f, 15.747f, 20.355f, 16.097f, 20.474f, 16.843f, 20.557f, 17.364f,
        20.177f, 17.774f, 19.884f, 18.154f, 19.384f, 18.745f, 18.1557f, 20.094f,
        16.5798f, 21.079f, 14.829f, 21.592f, 12.3806f, 22.3056f, 9.7523f, 22.0602f,
        7.4784f, 20.9056f, 5.2045f, 19.751f, 3.4555f, 17.7738f, 2.587f, 15.376f,
        0.395f, 9.368f, 4.496f, 2.771f, 10.846f, 2.065f, 12.1131f, 1.9187f,
        13.3965f, 2.0161f, 14.627f, 2.352f, 16.2553f, 2.8f, 17.7444f, 3.6511f,
        18.9568f, 4.8266f, 20.1693f, 6.0022f, 21.0659f, 7.4643f, 21.564f, 9.078f,
        21.783f, 9.779f, 21.915f, 10.507f, 21.973f, 11.239f, 22.03f, 11.959f,
        22.049f, 12.772f, 21.174f, 12.991f, 20.855f, 13.071f, 20.566f, 13f,
        20.289f, 12.84f, 17.616f, 11.038f, 17.37f, 10.806f, 17.121f, 10.506f,
        17.067f, 10.076f, 17.213f, 9.717f, 17.633f, 8.68f, 18.715f, 9.107f,
        19.521f, 9.287f,
    ];

    private static ReadOnlySpan<byte> RegularSystemRefresh1Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/refresh_2
    internal static IconOutline RegularSystemRefresh2 => new("regular/system/refresh_2", RegularSystemRefresh2Points, RegularSystemRefresh2Types);

    private static ReadOnlySpan<float> RegularSystemRefresh2Points =>
    [
        4.481f, 14.719f, 5.854f, 18.7f, 10.301f, 20.867f, 14.299f, 19.663f,
        15.8812f, 19.1845f, 17.2779f, 18.2311f, 18.3f, 16.932f, 18.5575f, 16.6033f,
        18.7889f, 16.2549f, 18.992f, 15.89f, 19.18f, 15.551f, 19.296f, 15.127f,
        19.641f, 14.9f, 20.273f, 14.486f, 21.144f, 14.925f, 21.187f, 15.68f,
        21.21f, 16.083f, 20.935f, 16.503f, 20.76f, 16.826f, 20.1329f, 17.9634f,
        19.2902f, 18.9678f, 18.279f, 19.783f, 14.257f, 23.058f, 8.201f, 22.64f,
        4.684f, 18.818f, 3.2998f, 17.315f, 2.3989f, 15.431f, 2.098f, 13.41f,
        2.0337f, 12.98f, 2.0006f, 12.5458f, 1.999f, 12.111f, 1.999f, 11.781f,
        2.119f, 11.475f, 2.37f, 11.25f, 2.756f, 10.906f, 3.28f, 10.911f,
        3.711f, 11.16f, 6.388f, 12.964f, 6.533f, 13.077f, 6.655f, 13.196f,
        6.744f, 13.357f, 7.125f, 14.051f, 6.607f, 14.952f, 5.818f, 14.972f,
        5.443f, 14.982f, 4.61f, 14.748f, 4.481f, 14.719f, 19.519f, 9.281f,
        18.146f, 5.3f, 13.699f, 3.134f, 9.701f, 4.337f, 8.1188f, 4.8155f,
        6.7221f, 5.7689f, 5.7f, 7.068f, 5.4425f, 7.3967f, 5.2111f, 7.7451f,
        5.008f, 8.11f, 4.82f, 8.449f, 4.704f, 8.874f, 4.359f, 9.1f,
        3.727f, 9.514f, 2.856f, 9.075f, 2.813f, 8.32f, 2.79f, 7.918f,
        3.065f, 7.497f, 3.24f, 7.175f, 3.867f, 6.0372f, 4.7097f, 5.0325f,
        5.721f, 4.217f, 9.743f, 0.942f, 15.799f, 1.361f, 19.316f, 5.182f,
        20.7002f, 6.685f, 21.6011f, 8.569f, 21.902f, 10.59f, 21.965f, 11.008f,
        22.002f, 11.465f, 22.001f, 11.889f, 22.001f, 12.219f, 21.881f, 12.526f,
        21.63f, 12.75f, 21.244f, 13.095f, 20.72f, 13.089f, 20.29f, 12.84f,
        17.612f, 11.036f, 17.4676f, 10.9309f, 17.3464f, 10.7971f, 17.256f, 10.643f,
        16.875f, 9.949f, 17.393f, 9.049f, 18.182f, 9.028f, 18.557f, 9.018f,
        19.39f, 9.253f, 19.519f, 9.281f,
    ];

    private static ReadOnlySpan<byte> RegularSystemRefresh2Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/safe_shield
    internal static IconOutline RegularSystemSafeShield => new("regular/system/safe_shield", RegularSystemSafeShieldPoints, RegularSystemSafeShieldTypes);

    private static ReadOnlySpan<float> RegularSystemSafeShieldPoints =>
    [
        12.407f, 7.483f, 15.363f, 8.592f, 15.63f, 8.716f, 15.785f, 8.82f,
        15.96f, 9.072f, 16.135f, 9.324f, 16.177f, 9.506f, 16.2f, 9.799f,
        16.2f, 12.052f, 16.149f, 13.674f, 15.334f, 14.993f, 13.906f, 15.763f,
        12.432f, 16.5f, 12f, 16.602f, 11.567f, 16.5f, 10.094f, 15.763f,
        8.666f, 14.993f, 7.85f, 13.674f, 7.8f, 12.052f, 7.8f, 9.799f,
        7.822f, 9.506f, 7.865f, 9.324f, 8.04f, 9.072f, 8.215f, 8.819f,
        8.37f, 8.716f, 8.636f, 8.592f, 11.592f, 7.483f, 11.95f, 7.385f,
        12.05f, 7.385f, 12.407f, 7.483f, 9.8f, 10.291f, 9.8f, 11.989f,
        9.823f, 12.737f, 10.145f, 13.426f, 10.779f, 13.848f, 11.055f, 14.033f,
        11.985f, 14.473f, 12f, 14.48f, 12.015f, 14.473f, 12.944f, 14.033f,
        13.221f, 13.848f, 13.855f, 13.426f, 14.176f, 12.737f, 14.2f, 11.989f,
        14.2f, 10.291f, 12f, 9.466f, 12.646f, 2.174f, 19.75f, 4.838f,
        20.239f, 5.051f, 20.629f, 5.405f, 20.842f, 5.9f, 20.959f, 6.174f,
        20.985f, 6.352f, 21f, 6.642f, 21f, 12.077f, 20.924f, 15.579f,
        19.142f, 18.462f, 16.044f, 20.096f, 12.601f, 21.817f, 12.302f, 21.939f,
        12.101f, 21.987f, 11.768f, 21.935f, 11.399f, 21.817f, 7.956f, 20.096f,
        4.858f, 18.462f, 3.076f, 15.579f, 3f, 12.077f, 3f, 6.642f,
        3.02f, 6.245f, 3.123f, 5.881f, 3.352f, 5.551f, 3.581f, 5.221f,
        3.885f, 4.997f, 4.25f, 4.838f, 11.354f, 2.174f, 11.774f, 2.0382f,
        12.226f, 2.0382f, 12.646f, 2.174f, 5.005f, 6.696f, 5f, 6.744f,
        5f, 12.034f, 5.053f, 14.495f, 6.216f, 16.719f, 8.338f, 18.021f,
        8.889f, 18.326f, 12f, 19.882f, 15.111f, 18.326f, 15.662f, 18.021f,
        17.784f, 16.719f, 18.947f, 14.495f, 19f, 12.034f, 19f, 6.744f,
        18.995f, 6.696f, 18.952f, 6.675f, 12.056f, 4.089f, 11.92f, 4.071f,
        12.08f, 4.071f, 11.944f, 4.089f, 5.048f, 6.675f,
    ];

    private static ReadOnlySpan<byte> RegularSystemSafeShieldTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 1, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 129, 0, 1,
        3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3,
        3, 3, 1, 3, 3, 3, 0, 1, 1, 3, 3, 3, 1, 1, 1, 1, 3, 3, 3, 1, 1, 1, 1, 3,
        3, 3, 129,
    ];

    // regular/system/safe_shield_2
    internal static IconOutline RegularSystemSafeShield2 => new("regular/system/safe_shield_2", RegularSystemSafeShield2Points, RegularSystemSafeShield2Types);

    private static ReadOnlySpan<float> RegularSystemSafeShield2Points =>
    [
        19.75f, 4.838f, 20.1093f, 4.9873f, 20.4182f, 5.2367f, 20.6398f, 5.5565f,
        20.8614f, 5.8763f, 20.9865f, 6.2531f, 21f, 6.642f, 21f, 12.077f,
        20.967f, 12.836f, 20.692f, 16.009f, 18.836f, 18.624f, 16.044f, 20.096f,
        12.601f, 21.817f, 12.302f, 21.939f, 12.101f, 21.987f, 11.768f, 21.935f,
        11.399f, 21.817f, 7.956f, 20.096f, 7.045f, 19.616f, 6.217f, 19.03f,
        5.502f, 18.285f, 3.873f, 16.585f, 3.051f, 14.415f, 3f, 12.077f,
        3f, 6.642f, 3.02f, 6.245f, 3.123f, 5.881f, 3.352f, 5.551f,
        3.581f, 5.221f, 3.885f, 4.997f, 4.25f, 4.838f, 11.354f, 2.174f,
        11.761f, 2.075f, 12.109f, 2.033f, 12.321f, 2.074f, 12.646f, 2.174f,
        5.005f, 6.696f, 5f, 6.744f, 5f, 12.034f, 5.049f, 14.284f,
        6.011f, 16.334f, 7.84f, 17.684f, 8.741f, 18.35f, 10.097f, 18.93f,
        11f, 19.382f, 11f, 4.443f, 13f, 19.382f, 13.903f, 18.93f,
        15.259f, 18.35f, 16.16f, 17.684f, 17.989f, 16.334f, 18.951f, 14.284f,
        19f, 12.034f, 19f, 6.744f, 18.995f, 6.696f, 13f, 4.443f,
    ];

    private static ReadOnlySpan<byte> RegularSystemSafeShield2Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3, 3,
        1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 131, 0, 1, 1, 3, 3, 3, 3, 3, 3, 129, 0, 3,
        3, 3, 3, 3, 3, 1, 1, 129,
    ];

    // regular/system/settings_3
    internal static IconOutline RegularSystemSettings3 => new("regular/system/settings_3", RegularSystemSettings3Points, RegularSystemSettings3Types);

    private static ReadOnlySpan<float> RegularSystemSettings3Points =>
    [
        12.474f, 8.03f, 13.2142f, 8.113f, 13.9152f, 8.4062f, 14.494f, 8.875f,
        17.614f, 11.415f, 15.523f, 16.397f, 11.526f, 15.971f, 10.7858f, 15.888f,
        10.0848f, 15.5948f, 9.506f, 15.126f, 6.386f, 12.586f, 8.477f, 7.604f,
        12.474f, 8.03f, 11.727f, 10.021f, 11.297f, 10.079f, 10.894f, 10.281f,
        10.587f, 10.588f, 9.242f, 11.933f, 10.392f, 14.234f, 12.273f, 13.98f,
        12.703f, 13.922f, 13.106f, 13.72f, 13.413f, 13.413f, 14.758f, 12.068f,
        13.608f, 9.767f, 11.727f, 10.021f, 15.102f, 2.533f, 15.516f, 2.587f,
        15.911f, 2.784f, 16.28f, 2.962f, 16.806f, 3.216f, 17.218f, 3.455f,
        17.7f, 3.785f, 18.06f, 4.031f, 18.513f, 4.323f, 18.746f, 4.722f,
        18.991f, 5.143f, 18.926f, 5.555f, 18.923f, 6.018f, 18.9126f, 6.6242f,
        19.0645f, 7.2222f, 19.363f, 7.75f, 19.604f, 8.168f, 19.939f, 8.523f,
        20.333f, 8.801f, 20.938f, 9.228f, 21.567f, 9.32f, 21.807f, 10.11f,
        22.065f, 10.961f, 22.088f, 13.23f, 21.75f, 14.048f, 21.667f, 14.248f,
        21.533f, 14.435f, 21.371f, 14.58f, 21.158f, 14.769f, 20.887f, 14.866f,
        20.643f, 15.005f, 20.1128f, 15.2991f, 19.6708f, 15.7296f, 19.363f, 16.252f,
        19.0645f, 16.7798f, 18.9126f, 17.3778f, 18.923f, 17.984f, 18.925f, 18.264f,
        18.976f, 18.547f, 18.918f, 18.826f, 18.8727f, 19.0406f, 18.7809f, 19.2427f,
        18.649f, 19.418f, 18.107f, 20.125f, 16.134f, 21.239f, 15.265f, 21.441f,
        14.462f, 21.628f, 14.067f, 21.128f, 13.395f, 20.818f, 12.9586f, 20.6123f,
        12.4824f, 20.5048f, 12f, 20.503f, 11.518f, 20.503f, 11.042f, 20.616f,
        10.605f, 20.818f, 9.813f, 21.184f, 9.471f, 21.713f, 8.523f, 21.379f,
        7.734f, 21.1f, 6.988f, 20.689f, 6.3f, 20.218f, 5.816f, 19.887f,
        5.313f, 19.55f, 5.124f, 18.974f, 5.016f, 18.643f, 5.075f, 18.326f,
        5.077f, 17.984f, 5.0874f, 17.3778f, 4.9355f, 16.7798f, 4.637f, 16.252f,
        4.3943f, 15.8353f, 4.0632f, 15.4769f, 3.667f, 15.202f, 3.062f, 14.775f,
        2.433f, 14.682f, 2.193f, 13.893f, 1.93f, 13.025f, 1.93f, 10.977f,
        2.193f, 10.109f, 2.26f, 9.89f, 2.369f, 9.705f, 2.522f, 9.535f,
        2.754f, 9.274f, 3.059f, 9.167f, 3.356f, 8.998f, 3.776f, 8.759f,
        4.151f, 8.442f, 4.439f, 8.053f, 4.7954f, 7.5649f, 5.0109f, 6.9882f,
        5.062f, 6.386f, 5.12f, 5.749f, 4.93f, 5.134f, 5.351f, 4.584f,
        5.603f, 4.256f, 5.965f, 4.014f, 6.3f, 3.785f, 6.782f, 3.455f,
        7.194f, 3.216f, 7.72f, 2.962f, 8.116f, 2.771f, 8.6f, 2.52f,
        9.067f, 2.518f, 9.554f, 2.516f, 9.879f, 2.779f, 10.279f, 3.013f,
        10.7989f, 3.326f, 11.3932f, 3.4942f, 12f, 3.5f, 12.482f, 3.5f,
        12.958f, 3.386f, 13.395f, 3.184f, 13.976f, 2.916f, 14.414f, 2.443f,
        15.102f, 2.533f, 7.064f, 5.715f, 7.183f, 7.93f, 6.066f, 9.866f,
        4.087f, 10.87f, 3.9885f, 11.6211f, 3.9885f, 12.3819f, 4.087f, 13.133f,
        6.066f, 14.136f, 7.183f, 16.072f, 7.064f, 18.287f, 7.674f, 18.755f,
        8.312f, 19.125f, 9.022f, 19.42f, 10.882f, 18.208f, 13.118f, 18.208f,
        14.978f, 19.42f, 15.6777f, 19.1302f, 16.3361f, 18.7492f, 16.936f, 18.287f,
        16.817f, 16.072f, 17.934f, 14.137f, 19.913f, 13.133f, 20.0115f, 12.3819f,
        20.0115f, 11.6211f, 19.913f, 10.87f, 17.934f, 9.866f, 16.817f, 7.93f,
        16.936f, 5.715f, 16.3358f, 5.2527f, 15.6771f, 4.8717f, 14.977f, 4.582f,
        13.118f, 5.794f, 10.882f, 5.794f, 9.023f, 4.582f, 8.3229f, 4.8717f,
        7.6642f, 5.2527f, 7.064f, 5.715f,
    ];

    private static ReadOnlySpan<byte> RegularSystemSettings3Types =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/time
    internal static IconOutline RegularSystemTime => new("regular/system/time", RegularSystemTimePoints, RegularSystemTimeTypes);

    private static ReadOnlySpan<float> RegularSystemTimePoints =>
    [
        13f, 11.586f, 15.707f, 14.293f, 16.066f, 14.702f, 16.111f, 15.303f,
        15.707f, 15.707f, 15.303f, 16.111f, 14.702f, 16.066f, 14.293f, 15.707f,
        11.293f, 12.707f, 11.134f, 12.5f, 11.033f, 12.325f, 11.013f, 12.198f,
        11f, 12f, 11f, 7f, 11.03f, 6.549f, 11.288f, 6.156f,
        11.741f, 6.034f, 12.397f, 5.858f, 12.958f, 6.361f, 13f, 7f,
        11.258f, 21.972f, 9.508f, 21.853f, 7.812f, 21.241f, 6.363f, 20.26f,
        1.054f, 16.668f, 0.538f, 9.075f, 5.166f, 4.701f, 6.0777f, 3.8393f,
        7.1464f, 3.1607f, 8.314f, 2.702f, 9.7218f, 2.152f, 11.2343f, 1.9218f,
        12.742f, 2.028f, 14.492f, 2.147f, 16.188f, 2.759f, 17.637f, 3.74f,
        22.946f, 7.332f, 23.462f, 14.925f, 18.834f, 19.299f, 17.9223f, 20.1607f,
        16.8536f, 20.8393f, 15.686f, 21.298f, 14.2782f, 21.848f, 12.7657f, 22.0782f,
        11.258f, 21.972f, 11.374f, 4.025f, 9.7742f, 4.1506f, 8.2492f, 4.7544f,
        6.997f, 5.758f, 2.03f, 9.744f, 3.51f, 17.709f, 9.585f, 19.629f,
        10.8402f, 20.0254f, 12.1738f, 20.1063f, 13.4677f, 19.8644f, 14.7616f, 19.6224f,
        15.9758f, 19.0652f, 17.003f, 18.242f, 21.97f, 14.257f, 20.49f, 6.291f,
        14.415f, 4.371f, 13.4325f, 4.0635f, 12.4004f, 3.946f, 11.374f, 4.025f,
    ];

    private static ReadOnlySpan<byte> RegularSystemTimeTypes =>
    [
        0, 1, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 131, 0, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/tool
    internal static IconOutline RegularSystemTool => new("regular/system/tool", RegularSystemToolPoints, RegularSystemToolTypes);

    private static ReadOnlySpan<float> RegularSystemToolPoints =>
    [
        11.468f, 16.071f, 10.721f, 16.272f, 9.979f, 16.409f, 9.201f, 16.379f,
        6.131f, 16.26f, 3.397f, 14.178f, 2.634f, 11.166f, 2.287f, 9.797f,
        2.327f, 8.325f, 2.822f, 6.995f, 3.044f, 6.399f, 3.293f, 5.755f,
        4.055f, 5.812f, 4.32f, 5.832f, 4.529f, 5.953f, 4.717f, 6.132f,
        8.169f, 9.882f, 9.615f, 9.595f, 9.904f, 8.146f, 6.153f, 4.697f,
        5.973f, 4.509f, 5.852f, 4.3f, 5.833f, 4.036f, 5.775f, 3.273f,
        6.419f, 3.024f, 7.015f, 2.802f, 8.345f, 2.307f, 9.817f, 2.267f,
        11.186f, 2.614f, 14.198f, 3.377f, 16.28f, 6.111f, 16.399f, 9.181f,
        16.429f, 9.959f, 16.292f, 10.701f, 16.091f, 11.448f, 20.487f, 15.167f,
        21.073f, 15.694f, 21.527f, 16.36f, 21.707f, 17.136f, 21.8962f, 17.9161f,
        21.8315f, 18.7362f, 21.5223f, 19.477f, 21.2131f, 20.2177f, 20.6756f, 20.8404f,
        19.9879f, 21.2545f, 19.3002f, 21.6685f, 18.4983f, 21.8523f, 17.699f, 21.779f,
        17.0905f, 21.7275f, 16.5052f, 21.522f, 15.998f, 21.182f, 15.6993f, 20.9779f,
        15.4269f, 20.7378f, 15.187f, 20.467f, 8.81f, 4.423f, 11.672f, 7.054f,
        11.806f, 7.195f, 11.885f, 7.293f, 11.946f, 7.48f, 12.007f, 7.668f,
        12.002f, 7.794f, 11.976f, 7.986f, 11.446f, 10.641f, 11.4026f, 10.8328f,
        11.3057f, 11.0083f, 11.1666f, 11.1472f, 11.0275f, 11.2862f, 10.8518f, 11.3828f,
        10.66f, 11.426f, 8.008f, 11.953f, 7.816f, 11.979f, 7.69f, 11.984f,
        7.503f, 11.923f, 7.316f, 11.862f, 7.218f, 11.784f, 7.078f, 11.65f,
        4.443f, 8.788f, 4.401f, 9.265f, 4.392f, 9.724f, 4.471f, 10.199f,
        4.857f, 12.543f, 6.922f, 14.33f, 9.296f, 14.38f, 9.8317f, 14.3919f,
        10.3659f, 14.3176f, 10.878f, 14.16f, 11.416f, 13.994f, 11.839f, 13.703f,
        12.365f, 14.082f, 12.544f, 14.247f, 16.689f, 19.145f, 17.393f, 19.915f,
        18.529f, 20.052f, 19.3f, 19.28f, 20.072f, 18.509f, 19.935f, 17.373f,
        19.165f, 16.669f, 14.267f, 12.524f, 14.102f, 12.345f, 13.723f, 11.819f,
        14.014f, 11.396f, 14.18f, 10.858f, 14.3376f, 10.3459f, 14.4119f, 9.8117f,
        14.4f, 9.276f, 14.35f, 6.902f, 12.563f, 4.837f, 10.219f, 4.451f,
        9.745f, 4.373f, 9.286f, 4.381f, 8.81f, 4.423f,
    ];

    private static ReadOnlySpan<byte> RegularSystemToolTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 131, 0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3,
        3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3,
        3, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/system/warning
    internal static IconOutline RegularSystemWarning => new("regular/system/warning", RegularSystemWarningPoints, RegularSystemWarningTypes);

    private static ReadOnlySpan<float> RegularSystemWarningPoints =>
    [
        11.741f, 16.966f, 11.4849f, 16.8973f, 11.2665f, 16.7297f, 11.134f, 16.5f,
        10.695f, 15.74f, 11.424f, 14.81f, 12.261f, 15.034f, 12.515f, 15.102f,
        12.736f, 15.272f, 12.868f, 15.5f, 13.307f, 16.26f, 12.578f, 17.19f,
        11.741f, 16.966f, 11.034f, 6.741f, 11.296f, 5.763f, 12.704f, 5.763f,
        12.966f, 6.741f, 13f, 7f, 13f, 13f, 12.97f, 13.451f,
        12.712f, 13.844f, 12.259f, 13.966f, 11.603f, 14.142f, 11.042f, 13.639f,
        11f, 13f, 11f, 7f, 18.809f, 19.325f, 17.7247f, 20.334f,
        16.4299f, 21.0895f, 15.018f, 21.537f, 13.5434f, 21.9996f, 11.9824f, 22.1176f,
        10.455f, 21.882f, 9.2196f, 21.689f, 8.0314f, 21.266f, 6.952f, 20.635f,
        1.179f, 17.254f, 0.289f, 9.233f, 5.192f, 4.675f, 6.276f, 3.6661f,
        7.5705f, 2.9106f, 8.982f, 2.463f, 10.4566f, 2.0004f, 12.0176f, 1.8824f,
        13.545f, 2.118f, 14.7804f, 2.311f, 15.9686f, 2.734f, 17.048f, 3.365f,
        22.821f, 6.746f, 23.711f, 14.767f, 18.809f, 19.325f, 11.374f, 4.025f,
        9.7742f, 4.1506f, 8.2492f, 4.7544f, 6.997f, 5.758f, 2.03f, 9.744f,
        3.51f, 17.709f, 9.585f, 19.629f, 10.8402f, 20.0254f, 12.1738f, 20.1063f,
        13.4677f, 19.8644f, 14.7616f, 19.6224f, 15.9758f, 19.0652f, 17.003f, 18.242f,
        21.97f, 14.257f, 20.49f, 6.291f, 14.415f, 4.371f, 13.4325f, 4.0635f,
        12.4004f, 3.946f, 11.374f, 4.025f,
    ];

    private static ReadOnlySpan<byte> RegularSystemWarningTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 1, 1, 3, 3, 3, 3, 3,
        3, 129, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    // regular/transportation/rocket
    internal static IconOutline RegularTransportationRocket => new("regular/transportation/rocket", RegularTransportationRocketPoints, RegularTransportationRocketTypes);

    private static ReadOnlySpan<float> RegularTransportationRocketPoints =>
    [
        13.739f, 11.84f, 13.3042f, 11.7551f, 12.9099f, 11.5282f, 12.618f, 11.195f,
        11.378f, 9.776f, 12.651f, 7.561f, 14.501f, 7.922f, 14.934f, 8.006f,
        15.332f, 8.235f, 15.622f, 8.567f, 16.863f, 9.985f, 15.589f, 12.2f,
        13.739f, 11.84f, 9.924f, 18.314f, 9.02f, 19.214f, 7.121f, 19.625f,
        5.891f, 19.895f, 5.567f, 19.967f, 5.174f, 20.099f, 4.837f, 20.067f,
        4.6046f, 20.0456f, 4.3871f, 19.9436f, 4.222f, 19.7787f, 4.0569f, 19.6138f,
        3.9547f, 19.3963f, 3.933f, 19.164f, 3.902f, 18.829f, 4.033f, 18.441f,
        4.103f, 18.119f, 4.334f, 17.068f, 4.59f, 15.983f, 5.054f, 15.007f,
        5.219f, 14.661f, 5.416f, 14.349f, 5.687f, 14.077f, 5.1896f, 13.9042f,
        4.6855f, 13.751f, 4.176f, 13.618f, 3.948f, 13.558f, 3.71f, 13.514f,
        3.495f, 13.414f, 2.619f, 13.01f, 2.262f, 12.005f, 2.624f, 11.126f,
        3.496f, 9.222f, 3.675f, 8.868f, 3.927f, 8.565f, 4.267f, 8.355f,
        4.6666f, 8.1157f, 5.1349f, 8.0175f, 5.597f, 8.076f, 6.667f, 8.228f,
        6.762f, 8.212f, 6.694f, 8.24f, 6.739f, 8.219f, 6.794f, 8.16f,
        7.319f, 7.495f, 7.354f, 7.454f, 7.9027f, 6.7928f, 8.5062f, 6.1789f,
        9.158f, 5.619f, 11.493f, 3.638f, 14.459f, 2.538f, 17.531f, 2.761f,
        18.322f, 2.818f, 19.917f, 2.983f, 20.493f, 3.592f, 21.003f, 4.132f,
        21.213f, 5.774f, 21.273f, 6.484f, 21.535f, 9.603f, 20.464f, 12.545f,
        18.421f, 14.897f, 17.855f, 15.548f, 17.221f, 16.138f, 16.552f, 16.681f,
        16.509f, 16.716f, 15.83f, 17.231f, 15.769f, 17.285f, 15.748f, 17.33f,
        15.776f, 17.264f, 15.758f, 17.357f, 15.88f, 18.393f, 15.917f, 18.842f,
        15.834f, 19.283f, 15.597f, 19.67f, 15.3953f, 19.9907f, 15.1106f, 20.2509f,
        14.773f, 20.423f, 12.909f, 21.335f, 12.027f, 21.725f, 10.997f, 21.375f,
        10.584f, 20.488f, 10.482f, 20.271f, 10.437f, 20.031f, 10.376f, 19.802f,
        10.2438f, 19.3006f, 10.093f, 18.8042f, 9.924f, 18.314f, 5.318f, 10.062f,
        4.58f, 11.657f, 6.304f, 12.107f, 8.676f, 12.855f, 9.923f, 14.169f,
        11.203f, 15.518f, 11.87f, 17.62f, 12.335f, 19.389f, 13.888f, 18.623f,
        13.844f, 18.105f, 13.693f, 17.516f, 13.782f, 16.979f, 13.936f, 16.038f,
        14.59f, 15.692f, 15.278f, 15.139f, 15.879f, 14.629f, 16.4052f, 14.1579f,
        16.8887f, 13.6412f, 17.324f, 13.085f, 19.125f, 10.767f, 19.751f, 7.852f,
        18.989f, 5.021f, 16.166f, 4.301f, 13.272f, 4.974f, 10.96f, 6.735f,
        10.397f, 7.164f, 9.875f, 7.645f, 9.393f, 8.163f, 8.873f, 8.755f,
        8.307f, 9.434f, 7.946f, 10.081f, 6.998f, 10.214f, 6.446f, 10.291f,
        5.846f, 10.119f, 5.318f, 10.062f, 6.244f, 17.757f, 6.702f, 17.64f,
        7.16f, 17.514f, 7.606f, 17.354f, 8.198f, 17.142f, 8.815f, 16.918f,
        8.75f, 16.158f, 8.7263f, 15.8952f, 8.5998f, 15.6523f, 8.3978f, 15.4824f,
        8.1959f, 15.3125f, 7.935f, 15.2294f, 7.672f, 15.251f, 6.929f, 15.314f,
        6.737f, 16.102f, 6.544f, 16.696f,
    ];

    private static ReadOnlySpan<byte> RegularTransportationRocketTypes =>
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3,
        3, 3, 3, 1, 3, 3, 3, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 1, 1, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 0, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3, 0, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 131,
    ];

    // regular/weather/lightning
    internal static IconOutline RegularWeatherLightning => new("regular/weather/lightning", RegularWeatherLightningPoints, RegularWeatherLightningTypes);

    private static ReadOnlySpan<float> RegularWeatherLightningPoints =>
    [
        8.613f, 13f, 5f, 13f, 4.754f, 12.969f, 4.135f, 12.812f,
        3.875f, 12.162f, 4.086f, 11.594f, 8.086f, 2.594f, 8.276f, 2.226f,
        8.587f, 2.024f, 9f, 2f, 17f, 2f, 17.62f, 2.039f,
        18.115f, 2.566f, 17.978f, 3.208f, 17.894f, 3.447f, 15.618f, 8f,
        19f, 8f, 19.198f, 8.013f, 19.325f, 8.033f, 19.5f, 8.134f,
        20.088f, 8.474f, 20.129f, 9.226f, 19.707f, 9.707f, 7.707f, 21.707f,
        6.901f, 22.412f, 5.782f, 21.72f, 6.051f, 20.684f, 9.288f, 17.298f,
        16.586f, 10f, 14f, 10f, 13.228f, 9.951f, 12.804f, 9.265f,
        13.106f, 8.553f, 15.382f, 4f, 9.65f, 4f, 6.539f, 11f,
        10f, 11f, 10.716f, 11.048f, 11.129f, 11.622f, 10.949f, 12.316f,
    ];

    private static ReadOnlySpan<byte> RegularWeatherLightningTypes =>
    [
        0, 1, 1, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 1, 1, 1, 3, 3, 3, 3, 3, 3, 1,
        3, 3, 131, 0, 1, 1, 3, 3, 3, 1, 1, 1, 1, 3, 3, 131,
    ];
}
