// Runs the compiled module (msfs-wasm/prebuilt/stormatc_route.wasm) against a fake simulator and
// checks what it does: the SimConnect calls it makes and the route messages it publishes.
//
//   node msfs-wasm/test/module_host_test.js
//
// The structure offsets are those of FsPlannedRoute / FsEnrouteLeg for wasm32, as clang dumps them
// (clang-cl --target=wasm32-unknown-wasi -Xclang -fdump-record-layouts).

const fs = require('fs');
const path = require('path');
const assert = require('assert');

const wasmPath = path.join(__dirname, '..', 'prebuilt', 'stormatc_route.wasm');

// --- layouts -------------------------------------------------------------------------------------
const ROUTE = {
  size: 164, departureAirport: 0, departureRunway: 22, destinationAirport: 61, destinationRunway: 83,
  cruiseAltitude: 147, isVfr: 155, numEnrouteLegs: 156, enrouteLegs: 160,
  departure: 30, arrival: 91, arrivalTransition: 100,
  approach: 109, approachRunway: 113, approachSuffix: 121, approachTransition: 125,
};
const LEG = { size: 101, type: 0, fixIcao: 4, via: 26, name: 35, altitude: 39, lat: 47, lon: 55 };
const ICAO = { type: 0, region: 1, airport: 4, ident: 13 };

const EVENT_ID = 4;            // SIMCONNECT_RECV_ID_EVENT
const EVENT_FILENAME_ID = 6;   // SIMCONNECT_RECV_ID_EVENT_FILENAME
const EVENT_EVERY_SECOND = 1;
const EVENT_FLIGHT_PLAN_ACTIVATED = 2;

// --- the fake simulator ---------------------------------------------------------------------------
const calls = [];
const published = [];
const posts = [];
const log = [];
let memory;
let table;
let dispatchIndex = null;
let broadcastIndex = null;
let efbRoutePointer = 0;
let exports_;

function text(pointer, length) {
  const bytes = new Uint8Array(memory.buffer, pointer, length);
  const end = bytes.indexOf(0);
  return Buffer.from(bytes.subarray(0, end < 0 ? length : end)).toString('utf8');
}

const imports = {
  env: {
    SimConnect_Open: (handlePointer) => { calls.push('open'); new BigUint64Array(memory.buffer, handlePointer, 1)[0] = 1n; return 0; },
    SimConnect_MapClientDataNameToID: (h, namePointer, id) => { calls.push('map ' + text(namePointer, 64) + ' ' + id); return 0; },
    SimConnect_CreateClientData: (h, id, size) => { calls.push('create ' + id + ' ' + size); return 0; },
    SimConnect_AddToClientDataDefinition: (h, def, offset, size) => { calls.push('define ' + def + ' ' + offset + ' ' + size); return 0; },
    SimConnect_SubscribeToSystemEvent: (h, id, namePointer) => { calls.push('subscribe ' + text(namePointer, 64) + ' ' + id); return 0; },
    SimConnect_CallDispatch: (h, proc) => { calls.push('dispatch'); dispatchIndex = proc; return 0; },
    SimConnect_SetClientData: (h, area, def, flags, reserved, size, pointer) => {
      published.push({ area, def, size, json: text(pointer, size) });
      return 0;
    },
    SimConnect_Close: () => { calls.push('close'); return 0; },
    fsNetworkHttpRequestPost: (urlPointer, paramPointer) => {
      // FsNetworkHttpRequestParam (packed, wasm32): postField, headerOptions, headerOptionsSize, data, dataSize.
      const view = new DataView(memory.buffer);
      const headersPointer = view.getUint32(paramPointer + 4, true);
      const headerCount = view.getUint32(paramPointer + 8, true);
      const dataPointer = view.getUint32(paramPointer + 12, true);
      const dataSize = view.getUint32(paramPointer + 16, true);
      const headers = [];
      for (let i = 0; i < headerCount; i++) {
        headers.push(text(view.getUint32(headersPointer + i * 4, true), 256));
      }
      posts.push({ url: text(urlPointer, 256), headers, body: text(dataPointer, dataSize) });
      return 1n;
    },
    fsPlannedRouteRegisterForBroadcast: (callback) => { calls.push('register'); broadcastIndex = callback; return 1; },
    fsPlannedRouteUnregisterForBroadcast: () => { calls.push('unregister'); return 1; },
    fsPlannedRouteGetEfbRoute: () => efbRoutePointer,
    __stack_chk_fail: () => { throw new Error('stack check failed'); },
  },
  wasi_snapshot_preview1: {
    fd_close: () => 0,
    fd_seek: () => 0,
    fd_write: (fd, iovs, count, writtenPointer) => {
      const view = new DataView(memory.buffer);
      let total = 0;
      for (let i = 0; i < count; i++) {
        const base = view.getUint32(iovs + i * 8, true);
        const length = view.getUint32(iovs + i * 8 + 4, true);
        log.push(Buffer.from(new Uint8Array(memory.buffer, base, length)).toString('utf8'));
        total += length;
      }
      view.setUint32(writtenPointer, total, true);
      return 0;
    },
    commit_pages: () => 0,
  },
};

// --- building routes in the module's memory -------------------------------------------------------
function writeString(pointer, value, size) {
  const bytes = new Uint8Array(memory.buffer, pointer, size);
  bytes.fill(0);
  bytes.set(Buffer.from(value, 'utf8').subarray(0, size - 1));
}

function writeIcao(pointer, ident, type = 'A', region = 'LF') {
  writeString(pointer + ICAO.type, type, 1 + 1);
  new Uint8Array(memory.buffer)[pointer + ICAO.type] = type.charCodeAt(0);
  writeString(pointer + ICAO.region, region, 3);
  writeString(pointer + ICAO.airport, ident, 9);
  writeString(pointer + ICAO.ident, ident, 9);
}

function newRoute({ departure, destination, runway = 8, designator = 0, vfr = true, altitude = 2500, legs = [], sid, star, approach }) {
  const pointer = exports_.malloc(ROUTE.size);
  new Uint8Array(memory.buffer, pointer, ROUTE.size).fill(0);
  const view = new DataView(memory.buffer);
  writeIcao(pointer + ROUTE.departureAirport, departure);
  view.setInt32(pointer + ROUTE.departureRunway, runway, true);
  view.setInt32(pointer + ROUTE.departureRunway + 4, designator, true);
  if (destination) {
    writeIcao(pointer + ROUTE.destinationAirport, destination);
  }
  if (sid) {
    writeString(pointer + ROUTE.departure, sid, 9);
  }
  if (star) {
    writeString(pointer + ROUTE.arrival, star, 9);
  }
  if (approach) {
    view.setInt32(pointer + ROUTE.approach, approach.type, true);                // 4 = ILS
    view.setInt32(pointer + ROUTE.approachRunway, approach.runway, true);
    view.setInt32(pointer + ROUTE.approachRunway + 4, approach.designator, true);
    writeString(pointer + ROUTE.approachSuffix, approach.suffix, 2);
    writeString(pointer + ROUTE.approachTransition, approach.transition, 9);
  }
  view.setInt32(pointer + ROUTE.cruiseAltitude, altitude ? 1 : 0, true);   // 1 = feet
  view.setInt32(pointer + ROUTE.cruiseAltitude + 4, altitude, true);
  new Uint8Array(memory.buffer)[pointer + ROUTE.isVfr] = vfr ? 1 : 0;
  view.setInt32(pointer + ROUTE.numEnrouteLegs, legs.length, true);
  if (legs.length > 0) {
    // malloc may grow the memory, which detaches earlier views: take a fresh one after it.
    const legsPointer = exports_.malloc(LEG.size * legs.length);
    const fresh = new DataView(memory.buffer);
    new Uint8Array(memory.buffer, legsPointer, LEG.size * legs.length).fill(0);
    legs.forEach((leg, i) => {
      const at = legsPointer + i * LEG.size;
      fresh.setInt32(at + LEG.type, leg.lat === undefined ? 0 : 1, true);
      writeIcao(at + LEG.fixIcao, leg.ident || '', 'W');
      fresh.setFloat64(at + LEG.lat, leg.lat || 0, true);
      fresh.setFloat64(at + LEG.lon, leg.lon || 0, true);
    });
    fresh.setUint32(pointer + ROUTE.enrouteLegs, legsPointer, true);
  }
  return pointer;
}

function send(id, eventId, fileName) {
  const size = id === EVENT_FILENAME_ID ? 300 : 28;
  const pointer = exports_.malloc(size);
  new Uint8Array(memory.buffer, pointer, size).fill(0);
  const view = new DataView(memory.buffer);
  view.setUint32(pointer, size, true);
  view.setUint32(pointer + 8, id, true);
  view.setUint32(pointer + 16, eventId, true);
  if (fileName) {
    writeString(pointer + 24, fileName, 260);
  }
  table.get(dispatchIndex)(pointer, size, 0);
}

function broadcast(route) {
  table.get(broadcastIndex)(route, 0);
}

(async () => {
  const bytes = fs.readFileSync(wasmPath);

  // The simulator does not load a module that imports something it does not provide (it lists the
  // module as "Failed"): the module may import only the simulator's API and the few WASI functions
  // the other modules in a Community folder import too.
  const provided = new Set([
    'wasi_snapshot_preview1.fd_close', 'wasi_snapshot_preview1.fd_seek',
    'wasi_snapshot_preview1.fd_write', 'wasi_snapshot_preview1.commit_pages',
  ]);
  const unsupported = WebAssembly.Module.imports(new WebAssembly.Module(bytes))
    .map((i) => `${i.module}.${i.name}`)
    .filter((name) => !provided.has(name) && !/^env\.(SimConnect_|fs)/.test(name));
  assert.deepStrictEqual(unsupported, [], 'the module imports only what the simulator provides');

  const { instance } = await WebAssembly.instantiate(bytes, imports);
  exports_ = instance.exports;
  memory = exports_.memory;
  table = exports_.__indirect_function_table;
  assert.ok(exports_.module_init && exports_.module_deinit, 'module_init and module_deinit are exported');

  // 1. Start: a plan is already in the EFB.
  efbRoutePointer = newRoute({ departure: 'LFPL', destination: 'LFHP', runway: 8, legs: [] });
  exports_.module_init();

  assert.deepStrictEqual(calls.slice(0, 7), [
    'open', 'map StormATC.Route 1', 'create 1 8192', 'define 1 0 8192',
    'subscribe 1sec 1', 'subscribe FlightPlanActivated 2', 'subscribe FlightPlanDeactivated 3',
  ]);
  assert.ok(calls.includes('dispatch') && calls.includes('register'), 'dispatch and broadcast are registered');
  // The module greets first, so StormATC can tell that it was loaded, then publishes the EFB's plan.
  assert.strictEqual(published.length, 2, 'a greeting and the plan already in the EFB are published at start');
  assert.deepStrictEqual(JSON.parse(published[0].json), { seq: 1, source: 'hello', route: {} });
  published.shift();
  let message = JSON.parse(published[0].json);
  assert.strictEqual(message.source, 'efb');
  assert.strictEqual(published[0].size, 8192, 'the whole area is written');
  assert.strictEqual(message.route.departureAirport.ident, 'LFPL');
  assert.strictEqual(message.route.destinationAirport.ident, 'LFHP');
  assert.deepStrictEqual(message.route.departureRunway, { number: '08', designator: '' });
  assert.deepStrictEqual(message.route.cruiseAltitude, { isFlightLevel: false, altitude: 2500 });
  assert.strictEqual(message.route.isVfr, true);

  // Every message also goes to StormATC's local server, with the marker header it asks for.
  assert.strictEqual(posts.length, 2, 'the greeting and the plan are posted too');
  assert.strictEqual(posts[0].url, 'http://127.0.0.1:48765/api/flightplan');
  assert.deepStrictEqual(posts[0].headers, ['X-StormATC: 1', 'Content-Type: application/json']);
  assert.strictEqual(JSON.parse(posts[0].body).source, 'hello');
  assert.strictEqual(JSON.parse(posts[1].body).route.departureAirport.ident, 'LFPL');

  // 2. Nothing changes: the EFB's route is not published again.
  for (let i = 0; i < 4; i++) { send(EVENT_ID, EVENT_EVERY_SECOND); }
  assert.strictEqual(published.length, 1, 'an unchanged EFB route is not published again');

  // 3. The pilot edits the plan: it is published as "efb" within two seconds.
  efbRoutePointer = newRoute({ departure: 'LFPL', destination: 'LFPT', runway: 26, designator: 1, legs: [
    { ident: 'MELUN' }, { ident: '', lat: 48.9, lon: 2.2 }] });
  send(EVENT_ID, EVENT_EVERY_SECOND); send(EVENT_ID, EVENT_EVERY_SECOND);
  assert.strictEqual(published.length, 2);
  message = JSON.parse(published[1].json);
  assert.strictEqual(message.source, 'efb');
  assert.strictEqual(message.route.destinationAirport.ident, 'LFPT');
  assert.deepStrictEqual(message.route.departureRunway, { number: '26', designator: 'L' });
  assert.strictEqual(message.route.enroute.length, 2);
  assert.strictEqual(message.route.enroute[0].fixIcao.ident, 'MELUN');
  assert.deepStrictEqual([message.route.enroute[1].hasLatLon, message.route.enroute[1].lat, message.route.enroute[1].lon], [true, 48.9, 2.2]);
  assert.ok(message.seq > JSON.parse(published[0].json).seq, 'each message has a higher sequence number');

  // 4. "Send to ATC": the simulator announces a flight plan; the EFB's route is published as "atc",
  //    even though it did not change.
  send(EVENT_FILENAME_ID, EVENT_FLIGHT_PLAN_ACTIVATED, '.PLN');
  assert.strictEqual(published.length, 3);
  assert.strictEqual(JSON.parse(published[2].json).source, 'atc');
  assert.strictEqual(JSON.parse(published[2].json).route.destinationAirport.ident, 'LFPT');

  // ... and again: sending the same plan twice is two messages.
  send(EVENT_FILENAME_ID, EVENT_FLIGHT_PLAN_ACTIVATED, '.PLN');
  assert.strictEqual(published.length, 4);

  // 5. "Send to avionics": the broadcast carries the route.
  broadcast(newRoute({ departure: 'LFPL', destination: 'LFPN', vfr: false, altitude: 0,
    sid: 'PILU2F', star: 'VEVA7R', approach: { type: 4, runway: 4, designator: 2, suffix: 'Y', transition: 'NERA1' } }));
  assert.strictEqual(published.length, 5);
  message = JSON.parse(published[4].json);
  assert.strictEqual(message.source, 'avionics');
  assert.strictEqual(message.route.destinationAirport.ident, 'LFPN');
  assert.strictEqual(message.route.isVfr, false);
  assert.strictEqual(message.route.cruiseAltitude, null);
  // The procedures come through: SID, STAR and the approach with its runway, suffix and transition.
  assert.strictEqual(message.route.departure, 'PILU2F');
  assert.strictEqual(message.route.arrival, 'VEVA7R');
  assert.deepStrictEqual(message.route.approach,
    { type: 'ILS', runway: { number: '04', designator: 'R' }, suffix: 'Y', transition: 'NERA1' });

  // 6. A route with no departure (the EFB was cleared) is ignored.
  efbRoutePointer = newRoute({ departure: '', destination: '' });
  send(EVENT_ID, EVENT_EVERY_SECOND); send(EVENT_ID, EVENT_EVERY_SECOND);
  send(EVENT_FILENAME_ID, EVENT_FLIGHT_PLAN_ACTIVATED, '.PLN');
  broadcast(newRoute({ departure: '', destination: '' }));
  assert.strictEqual(published.length, 5, 'empty routes are not published');

  // 7. No EFB route at all (null) and a null broadcast do not crash.
  efbRoutePointer = 0;
  send(EVENT_ID, EVENT_EVERY_SECOND); send(EVENT_ID, EVENT_EVERY_SECOND);
  broadcast(0);
  assert.strictEqual(published.length, 5);

  // 8. A very long route is cut cleanly and the message is still JSON.
  efbRoutePointer = newRoute({ departure: 'LFPL', destination: 'LFPT',
    legs: Array.from({ length: 400 }, (_, i) => ({ ident: 'WP' + String(i).padStart(3, '0'), lat: 48 + i / 1000, lon: 2 })) });
  send(EVENT_FILENAME_ID, EVENT_FLIGHT_PLAN_ACTIVATED, '.PLN');
  message = JSON.parse(published[5].json);
  assert.ok(message.route.enroute.length > 20 && message.route.enroute.length < 400, 'long route cut: ' + message.route.enroute.length);

  // The HTTP road carries exactly what the SimConnect road carries.
  assert.strictEqual(posts.length, published.length + 1, 'one post per message, and the greeting');
  assert.strictEqual(JSON.parse(posts[posts.length - 1].body).seq, JSON.parse(published[published.length - 1].json).seq);

  // 9. Stop.
  exports_.module_deinit();
  assert.ok(calls.includes('unregister') && calls.includes('close'));

  console.log('module ok: ' + published.length + ' messages, ' + calls.length + ' calls');
})().catch((error) => { console.error(error); process.exit(1); });
