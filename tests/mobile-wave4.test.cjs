const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..', 'src', 'IPop.Web');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');

test('Directory uses phone-only screen and preserves desktop body and existing contact actions', () => {
  const source = read('Pages/Directory.razor');
  assert.match(source, /Phone\.IsPhone/);
  assert.match(source, /<PeoplePhone/);
  assert.match(source, /<DepartmentsPhone/);
  assert.match(source, /<PersonPhone/);
  assert.match(source, /<ProfileDrawer Person="selected"/);
  assert.match(source, /dir-toolbar/);
});

test('Dashboard renders phone branch using real data and account preference endpoint', () => {
  const source = read('Pages/Dashboard.razor');
  assert.ok(source.includes('Phone.IsPhone'), 'phone-only branch');
  assert.ok(source.includes('<DashboardPhone'), 'widget screen');
  assert.ok(source.includes('/api/me/ui-prefs/dash-order'), 'server-saved order');
  assert.ok(source.includes('CanPostNews="@(data?.CanPostNews == true)"'), 'publish permission retained');
  assert.ok(source.includes('@if (data.IsAdmin && data.Status.Count > 0)'), 'system status remains admin only');
  assert.ok(source.includes('$"/news?post={n.Id}"'), 'phone news uses existing reading route');
});

test('Calendar phone agenda retains shared privacy, kind picker, recurrence and meeting detail paths', () => {
  const source = read('Pages/Calendar.razor');
  assert.ok(source.includes('<WeekStripAgenda'), 'phone agenda is wired');
  assert.ok(source.includes('<NewEventSheet'), 'phone sheet reuses existing form');
  for (const token of ['MeetingKindPicker', 'MeetingDetail', 'CalendarSettingsSheet', 'scopeTcs', 'SaveAsync']) assert.ok(source.includes(token), token);
});

test('Directory profile route uses PhoneScreen while retaining desktop profile', () => {
  const source = read('Pages/EmployeeProfile.razor');
  assert.match(source, /Phone\.IsPhone/);
  assert.match(source, /<PersonPhone/);
  assert.match(source, /emp-profile/);
});
