// Native test of route_json.h: builds routes the way the simulator hands them over and checks the
// JSON the module would publish. Build and run with scripts\Test-MsfsModule.ps1.

#include "../src/route_json.h"

#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(condition)                                                              \
    do                                                                                \
    {                                                                                 \
        if (!(condition))                                                             \
        {                                                                             \
            printf("FAILED line %d: %s\n", __LINE__, #condition);                     \
            failures++;                                                               \
        }                                                                             \
    } while (0)

static FsRouteIcao Icao(char type, const char* region, const char* airport, const char* ident)
{
    FsRouteIcao icao;
    memset(&icao, 0, sizeof(icao));
    icao.type = type;
    snprintf(icao.region, sizeof(icao.region), "%s", region);
    snprintf(icao.airport, sizeof(icao.airport), "%s", airport);
    snprintf(icao.ident, sizeof(icao.ident), "%s", ident);
    return icao;
}

static FsPlannedRoute VfrRoute(FsEnrouteLeg* legs, int count)
{
    FsPlannedRoute route;
    memset(&route, 0, sizeof(route));
    route.departureAirport = Icao('A', "LF", "LFPL", "LFPL");
    route.departureRunway.number = FsRunwayIdentifierNumber_8;
    route.departureRunway.designator = FsRunwayIdentifierDesignator_None;
    route.destinationAirport = Icao('A', "LF", "LFPT", "LFPT");
    route.destinationRunway.number = FsRunwayIdentifierNumber_23;
    route.destinationRunway.designator = FsRunwayIdentifierDesignator_Left;
    route.cruiseAltitude.type = FsFlightAltitudeType_Feet;
    route.cruiseAltitude.altitude = 2500;
    route.isVfr = true;
    route.numEnrouteLegs = count;
    route.enrouteLegs = legs;
    return route;
}

static FsEnrouteLeg Leg(const char* ident, double lat, double lon, FsEnrouteLegType type)
{
    FsEnrouteLeg leg;
    memset(&leg, 0, sizeof(leg));
    leg.type = type;
    leg.fixIcao = Icao('W', "LF", "", ident);
    leg.lat = lat;
    leg.lon = lon;
    return leg;
}

static bool Contains(const char* text, const char* part) { return strstr(text, part) != nullptr; }

static void AVfrRouteGivesItsAerodromesRunwaysAltitudeAndLegs()
{
    FsEnrouteLeg legs[2] = {Leg("MELUN", 0, 0, FsEnrouteLegType_Normal), Leg("", 48.9, 2.2, FsEnrouteLegType_LatLon)};
    FsPlannedRoute route = VfrRoute(legs, 2);
    char buffer[8192];

    CHECK(stormatc::WriteMessage(buffer, sizeof(buffer), 7, "atc", route));

    CHECK(Contains(buffer, "\"seq\":7"));
    CHECK(Contains(buffer, "\"source\":\"atc\""));
    CHECK(Contains(buffer, "\"departureAirport\":{\"type\":\"A\",\"region\":\"LF\",\"airport\":\"LFPL\",\"ident\":\"LFPL\"}"));
    CHECK(Contains(buffer, "\"departureRunway\":{\"number\":\"08\",\"designator\":\"\"}"));
    CHECK(Contains(buffer, "\"destinationRunway\":{\"number\":\"23\",\"designator\":\"L\"}"));
    CHECK(Contains(buffer, "\"cruiseAltitude\":{\"isFlightLevel\":false,\"altitude\":2500}"));
    CHECK(Contains(buffer, "\"isVfr\":true"));
    CHECK(Contains(buffer, "\"ident\":\"MELUN\""));
    CHECK(Contains(buffer, "\"hasLatLon\":true,\"lat\":48.9,\"lon\":2.2"));
}

static void AFlightLevelAndNoAltitudeAreTold()
{
    FsPlannedRoute route = VfrRoute(nullptr, 0);
    char buffer[2048];

    route.cruiseAltitude.type = FsFlightAltitudeType_FlightLevel;
    route.cruiseAltitude.altitude = 100;
    route.isVfr = false;
    CHECK(stormatc::WriteMessage(buffer, sizeof(buffer), 1, "efb", route));
    CHECK(Contains(buffer, "\"cruiseAltitude\":{\"isFlightLevel\":true,\"altitude\":100}"));
    CHECK(Contains(buffer, "\"isVfr\":false"));

    route.cruiseAltitude.type = FsFlightAltitudeType_None;
    CHECK(stormatc::WriteMessage(buffer, sizeof(buffer), 2, "efb", route));
    CHECK(Contains(buffer, "\"cruiseAltitude\":null"));
}

static void AnEmptyRouteIsStillValid()
{
    FsPlannedRoute route;
    memset(&route, 0, sizeof(route));
    char buffer[2048];

    CHECK(stormatc::WriteMessage(buffer, sizeof(buffer), 1, "efb", route));
    CHECK(Contains(buffer, "\"departureAirport\":{\"type\":\"\",\"region\":\"\",\"airport\":\"\",\"ident\":\"\"}"));
    CHECK(Contains(buffer, "\"enroute\":[]"));
}

static void QuotesAndControlCharactersAreEscapedAndNonAsciiIsDropped()
{
    FsEnrouteLeg legs[1] = {Leg("A\"B\\C", 0, 0, FsEnrouteLegType_Normal)};
    legs[0].fixIcao.ident[5] = '\x01';
    FsPlannedRoute route = VfrRoute(legs, 1);
    char buffer[2048];

    CHECK(stormatc::WriteMessage(buffer, sizeof(buffer), 1, "atc", route));
    CHECK(Contains(buffer, "\"ident\":\"A\\\"B\\\\C\""));
}

static void AnIdentThatIsNotTerminatedIsReadOnlyUpToItsSize()
{
    FsEnrouteLeg legs[1] = {Leg("", 0, 0, FsEnrouteLegType_Normal)};
    memset(legs[0].fixIcao.ident, 'X', sizeof(legs[0].fixIcao.ident));
    FsPlannedRoute route = VfrRoute(legs, 1);
    char buffer[2048];

    CHECK(stormatc::WriteMessage(buffer, sizeof(buffer), 1, "atc", route));
    CHECK(Contains(buffer, "\"ident\":\"XXXXXXXXX\""));
}

static void ATooLongRouteLosesItsLastLegsButKeepsItsStructure()
{
    static FsEnrouteLeg legs[400];
    for (int i = 0; i < 400; i++)
    {
        char ident[9];
        snprintf(ident, sizeof(ident), "WP%03d", i);
        legs[i] = Leg(ident, 48.0 + i * 0.001, 2.0, FsEnrouteLegType_Normal);
    }

    FsPlannedRoute route = VfrRoute(legs, 400);
    char buffer[8192];

    stormatc::WriteMessage(buffer, sizeof(buffer), 1, "atc", route);

    size_t length = strlen(buffer);
    CHECK(length < sizeof(buffer));
    CHECK(Contains(buffer, "\"ident\":\"WP000\""));
    CHECK(!Contains(buffer, "\"ident\":\"WP399\""));
    CHECK(length >= 3 && strcmp(buffer + length - 3, "]}}") == 0);
}

static void ABufferTooSmallNeverWritesPastItsEnd()
{
    FsPlannedRoute route = VfrRoute(nullptr, 0);
    char guarded[64 + 16];
    memset(guarded, 'Z', sizeof(guarded));

    stormatc::WriteMessage(guarded, 64, 1, "atc", route);

    for (size_t i = 64; i < sizeof(guarded); i++)
    {
        CHECK(guarded[i] == 'Z');
    }
}

static void TheHelloIsValidJson()
{
    char buffer[256];

    CHECK(stormatc::WriteHello(buffer, sizeof(buffer), 1));
    CHECK(strcmp(buffer, "{\"seq\":1,\"source\":\"hello\",\"route\":{}}") == 0);
}

int main()
{
    TheHelloIsValidJson();
    AVfrRouteGivesItsAerodromesRunwaysAltitudeAndLegs();
    AFlightLevelAndNoAltitudeAreTold();
    AnEmptyRouteIsStillValid();
    QuotesAndControlCharactersAreEscapedAndNonAsciiIsDropped();
    AnIdentThatIsNotTerminatedIsReadOnlyUpToItsSize();
    ATooLongRouteLosesItsLastLegsButKeepsItsStructure();
    ABufferTooSmallNeverWritesPastItsEnd();

    if (failures == 0)
    {
        printf("route_json: all checks passed\n");
        return 0;
    }

    printf("route_json: %d check(s) failed\n", failures);
    return 1;
}
