#ifndef CROSS_TU_CODEC_H
#define CROSS_TU_CODEC_H

typedef struct Packet
{
    int size;
} Packet;

int decode_audio(Packet* packet);
int decode_video(Packet* packet);

#endif
