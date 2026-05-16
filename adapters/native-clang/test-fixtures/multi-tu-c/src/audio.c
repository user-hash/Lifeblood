#include "packet.h"

static int audio_gain(int samples)
{
    return samples + 1;
}

int decode_audio(Packet* packet)
{
    return audio_gain(packet->size);
}
