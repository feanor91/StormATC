// Turns the route the simulator's EFB holds (FsPlannedRoute) into the JSON StormATC reads.
//
// The JSON has the shape of JS_FlightPlanRoute, the route as the simulator hands it to JavaScript,
// so that StormATC reads the module's routes and the panel's routes with the same parser:
//   departureAirport / destinationAirport { type, region, airport, ident }
//   departureRunway / destinationRunway   { number, designator }
//   enroute [ { fixIcao, via, hasLatLon, lat, lon, name } ]
//   cruiseAltitude { isFlightLevel, altitude } or null, isVfr
//
// Nothing here depends on the simulator: it only reads the plain structures of MSFS_PlannedRoute.h,
// so it can be compiled and tested on the desktop.

#pragma once

#include <MSFS/MSFS_PlannedRoute.h>

#include <stdio.h>
#include <string.h>

namespace stormatc
{

// Writes into a fixed buffer and stops cleanly when it is full: a route that is too long loses its
// last legs, never its structure.
class JsonBuffer
{
public:
    JsonBuffer(char* buffer, size_t capacity) : m_buffer(buffer), m_capacity(capacity)
    {
        if (m_capacity > 0)
        {
            m_buffer[0] = '\0';
        }
    }

    size_t length() const { return m_length; }
    bool overflowed() const { return m_overflowed; }

    // Room kept free so the closing brackets always fit after the last leg.
    size_t remaining() const { return m_capacity > m_length + 1 ? m_capacity - m_length - 1 : 0; }

    void raw(const char* text)
    {
        size_t size = strlen(text);
        if (size > remaining())
        {
            m_overflowed = true;
            return;
        }

        memcpy(m_buffer + m_length, text, size);
        m_length += size;
        m_buffer[m_length] = '\0';
    }

    // A JSON string: quotes, backslashes and control characters are escaped; bytes above 127 are
    // dropped (identifiers are ASCII).
    void text(const char* value, size_t maximum)
    {
        raw("\"");
        for (size_t i = 0; value != nullptr && i < maximum && value[i] != '\0'; i++)
        {
            unsigned char c = static_cast<unsigned char>(value[i]);
            if (c == '"' || c == '\\')
            {
                char pair[3] = {'\\', static_cast<char>(c), '\0'};
                raw(pair);
            }
            else if (c >= 0x20 && c < 0x7F)
            {
                char single[2] = {static_cast<char>(c), '\0'};
                raw(single);
            }
        }

        raw("\"");
    }

    void number(double value)
    {
        char text[40];
        snprintf(text, sizeof(text), "%.7g", value);
        raw(text);
    }

    void integer(long value)
    {
        char text[24];
        snprintf(text, sizeof(text), "%ld", value);
        raw(text);
    }

private:
    char* m_buffer;
    size_t m_capacity;
    size_t m_length = 0;
    bool m_overflowed = false;
};

inline void WriteIcao(JsonBuffer& json, const FsRouteIcao& icao)
{
    char type[2] = {icao.type >= 0x20 && icao.type < 0x7F ? icao.type : '\0', '\0'};

    json.raw("{\"type\":");
    json.text(type, 1);
    json.raw(",\"region\":");
    json.text(icao.region, sizeof(icao.region));
    json.raw(",\"airport\":");
    json.text(icao.airport, sizeof(icao.airport));
    json.raw(",\"ident\":");
    json.text(icao.ident, sizeof(icao.ident));
    json.raw("}");
}

inline void WriteRunway(JsonBuffer& json, const FsRunwayIdentifier& runway)
{
    // The number enumeration counts from 1 for runway 01; the designator is a suffix letter.
    char number[4] = "";
    if (static_cast<int>(runway.number) > 0 && static_cast<int>(runway.number) <= 36)
    {
        snprintf(number, sizeof(number), "%02d", static_cast<int>(runway.number));
    }

    const char* designator = "";
    switch (runway.designator)
    {
        case FsRunwayIdentifierDesignator_Left: designator = "L"; break;
        case FsRunwayIdentifierDesignator_Right: designator = "R"; break;
        case FsRunwayIdentifierDesignator_Center: designator = "C"; break;
        default: break;
    }

    json.raw("{\"number\":");
    json.text(number, sizeof(number));
    json.raw(",\"designator\":");
    json.text(designator, 1);
    json.raw("}");
}

inline void WriteLeg(JsonBuffer& json, const FsEnrouteLeg& leg)
{
    json.raw("{\"fixIcao\":");
    WriteIcao(json, leg.fixIcao);
    json.raw(",\"via\":");
    json.text(leg.via, sizeof(leg.via));
    json.raw(",\"hasLatLon\":");
    json.raw(leg.type == FsEnrouteLegType_LatLon ? "true" : "false");
    json.raw(",\"lat\":");
    json.number(leg.lat);
    json.raw(",\"lon\":");
    json.number(leg.lon);
    json.raw(",\"name\":");
    json.text(leg.name, 64);
    json.raw("}");
}

// The route object alone.
inline bool WriteRoute(JsonBuffer& json, const FsPlannedRoute& route)
{
    json.raw("{\"departureAirport\":");
    WriteIcao(json, route.departureAirport);
    json.raw(",\"departureRunway\":");
    WriteRunway(json, route.departureRunway);
    json.raw(",\"destinationAirport\":");
    WriteIcao(json, route.destinationAirport);
    json.raw(",\"destinationRunway\":");
    WriteRunway(json, route.destinationRunway);

    json.raw(",\"cruiseAltitude\":");
    if (route.cruiseAltitude.type == FsFlightAltitudeType_None)
    {
        json.raw("null");
    }
    else
    {
        json.raw("{\"isFlightLevel\":");
        json.raw(route.cruiseAltitude.type == FsFlightAltitudeType_FlightLevel ? "true" : "false");
        json.raw(",\"altitude\":");
        json.integer(route.cruiseAltitude.altitude);
        json.raw("}");
    }

    json.raw(",\"isVfr\":");
    json.raw(route.isVfr ? "true" : "false");

    // The legs go last and are added while there is room for one more and the closing brackets.
    json.raw(",\"enroute\":[");
    bool first = true;
    for (int i = 0; i < route.numEnrouteLegs && route.enrouteLegs != nullptr; i++)
    {
        char leg[512];
        JsonBuffer legJson(leg, sizeof(leg));
        WriteLeg(legJson, route.enrouteLegs[i]);

        // The closing "]}" of the route and "}" of the envelope still have to fit.
        if (legJson.overflowed() || legJson.length() + 8 > json.remaining())
        {
            break;
        }

        if (!first)
        {
            json.raw(",");
        }

        json.raw(leg);
        first = false;
    }

    json.raw("]}");
    return !json.overflowed();
}

// The message the module publishes: a sequence number, where the route came from, and the route.
inline bool WriteMessage(char* buffer, size_t capacity, long sequence, const char* source, const FsPlannedRoute& route)
{
    JsonBuffer json(buffer, capacity);
    json.raw("{\"seq\":");
    json.integer(sequence);
    json.raw(",\"source\":");
    json.text(source, 16);
    json.raw(",\"route\":");
    WriteRoute(json, route);
    json.raw("}");
    return !json.overflowed();
}

// The module's greeting, published once when it starts: it is running and has nothing to say yet.
// StormATC logs it, which tells the pilot that the module was loaded.
inline bool WriteHello(char* buffer, size_t capacity, long sequence)
{
    JsonBuffer json(buffer, capacity);
    json.raw("{\"seq\":");
    json.integer(sequence);
    json.raw(",\"source\":\"hello\",\"route\":{}}");
    return !json.overflowed();
}

} // namespace stormatc
