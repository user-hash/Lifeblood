#include "codec.h"

#if ENABLE_VIDEO
#define PROFILE_KIND 2

static int scale_video(int value)
{
    return value + PROFILE_KIND;
}

int decode_video(struct Packet *packet)
{
    return scale_video(packet->size + PACKET_BASE);
}
#else
#define PROFILE_KIND 1

static int scale_audio(int value)
{
    return value + PROFILE_KIND;
}

int decode_audio(struct Packet *packet)
{
    return scale_audio(packet->size + PACKET_BASE);
}
#endif
