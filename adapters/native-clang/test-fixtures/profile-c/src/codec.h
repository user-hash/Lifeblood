#ifndef PROFILE_CODEC_H
#define PROFILE_CODEC_H

#define PACKET_BASE 10

struct Packet {
    int size;
};

#if ENABLE_VIDEO
int decode_video(struct Packet *packet);
#else
int decode_audio(struct Packet *packet);
#endif

#endif
