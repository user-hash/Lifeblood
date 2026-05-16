struct Packet {
    int size;
};

struct Packet *current_packet(void)
{
    return 0;
}

struct Packet *echo_packet(struct Packet *packet)
{
    return packet;
}

struct Packet *packet_ring[2];

struct Packet *first_packet(struct Packet *packets[2])
{
    return packets[0];
}
