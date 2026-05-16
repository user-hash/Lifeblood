#include "codec.h"

int decode_video(Packet* packet)
{
    return decode_audio(packet) * 2;
}
