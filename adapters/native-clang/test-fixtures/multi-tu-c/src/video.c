#include "packet.h"

static int video_scale(int pixels)
{
    return pixels * 2;
}

int decode_video(Packet* packet)
{
    return video_scale(packet->size);
}
