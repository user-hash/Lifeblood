#include "registry.h"

static int decode_audio(struct Packet *packet)
{
    return packet->size;
}

static int decode_video(struct Packet *packet)
{
    return packet->size + 1;
}

struct CodecRegistration codec_table[] = {
    { "audio", decode_audio },
    { "video", decode_video }
};

int dispatch_first(struct Packet *packet)
{
    return codec_table[0].handler(packet);
}
