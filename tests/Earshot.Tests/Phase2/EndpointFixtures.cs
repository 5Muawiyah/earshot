using Earshot.Audio;
using Earshot.Contracts;

namespace Earshot.Tests.Phase2;

// Synthetic endpoint lists modelled on the read-only probe of the owner's machine: the AirPods render and
// capture endpoints in their own container, the internal Realtek and AMD endpoints in the PC container
// (several NOTPRESENT, some with an unreadable friendly name), a USB microphone in its own container, and
// a hypothetical endpoint for the paired iPhone in a different container. The real iPhone has a Hands-Free
// devnode but no Core Audio endpoint; the fixture adds one to prove a non-matching container is never
// chosen even if one appeared.
internal static class EndpointFixtures
{
    public const string AirPodsName = "Jonathan’s AirPods Pro - Find My";

    public static readonly Guid AirPodsContainer = new("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13");
    public static readonly Guid IPhoneContainer = new("7e2d4c8a-5a1e-4d0b-9c61-3f0e2a7b8c90");
    public static readonly Guid MicrophoneContainer = new("6a0d2c11-8f5e-4b7a-a9d3-2e41c7b05f18");
    public static readonly Guid PcContainer = NodeMatch.PcContainer;

    public const string AirPodsRenderId = "{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516970}";
    public const string AirPodsCaptureId = "{0.0.1.00000000}.{0b46d234-b82d-4b72-b995-e8e3ca2937c9}";

    public static EndpointReading AirPodsRender(EndpointState state = EndpointState.Active) =>
        new(new AudioEndpoint(AirPodsRenderId, EndpointFlow.Render, state, "Headphones (" + AirPodsName + ")", AirPodsContainer), AirPodsName);

    public static EndpointReading AirPodsCapture(EndpointState state = EndpointState.Active) =>
        new(new AudioEndpoint(AirPodsCaptureId, EndpointFlow.Capture, state, "Headset (" + AirPodsName + ")", AirPodsContainer), AirPodsName);

    public static EndpointReading RealtekSpeakers() =>
        new(new AudioEndpoint("{0.0.0.00000000}.{11111111-2222-4333-8444-555555555501}", EndpointFlow.Render, EndpointState.Active,
            "Speakers (Realtek(R) Audio)", PcContainer), "Realtek(R) Audio");

    public static EndpointReading RealtekMicrophone() =>
        new(new AudioEndpoint("{0.0.1.00000000}.{11111111-2222-4333-8444-555555555502}", EndpointFlow.Capture, EndpointState.Unplugged,
            "Microphone (Realtek(R) Audio)", PcContainer), "Realtek(R) Audio");

    public static EndpointReading StereoMix() =>
        new(new AudioEndpoint("{0.0.1.00000000}.{11111111-2222-4333-8444-555555555503}", EndpointFlow.Capture, EndpointState.Disabled,
            "Stereo Mix (Realtek(R) Audio)", PcContainer), "Realtek(R) Audio");

    // A NOTPRESENT AMD HDMI endpoint whose friendly name read failed with 0xE000020B.
    public static EndpointReading AmdHdmiUnreadable(int n) =>
        new(new AudioEndpoint("{0.0.0.00000000}.{aaaaaaaa-bbbb-4ccc-8ddd-0000000000" + n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "}",
            EndpointFlow.Render, EndpointState.NotPresent, null, PcContainer), null);

    public static EndpointReading UsbMicrophone() =>
        new(new AudioEndpoint("{0.0.1.00000000}.{33333333-4444-4555-8666-777777777701}", EndpointFlow.Capture, EndpointState.Active,
            "Microphone (Seiren Mini)", MicrophoneContainer), "Seiren Mini");

    public static EndpointReading IPhoneHandsFree() =>
        new(new AudioEndpoint("{0.0.1.00000000}.{44444444-5555-4666-8777-888888888801}", EndpointFlow.Capture, EndpointState.Active,
            "Headset (iPhone Hands-Free HF Audio)", IPhoneContainer), "iPhone Hands-Free HF Audio");

    // Everything on the machine, AirPods connected.
    public static List<EndpointReading> Machine(EndpointState airPodsRender = EndpointState.Active, EndpointState? airPodsCapture = EndpointState.Active)
    {
        var list = new List<EndpointReading>
        {
            RealtekSpeakers(),
            AmdHdmiUnreadable(1),
            RealtekMicrophone(),
            AirPodsRender(airPodsRender),
            StereoMix(),
            AmdHdmiUnreadable(2),
            UsbMicrophone(),
        };

        if (airPodsCapture is EndpointState captureState)
        {
            list.Add(AirPodsCapture(captureState));
        }

        return list;
    }
}
