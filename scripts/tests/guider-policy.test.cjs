const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
let rule;
vm.runInNewContext(fs.readFileSync(path.join(__dirname,
  '../../packaging/debian/usr/share/polkit-1/rules.d/50-openastroara-guider.rules'), 'utf8'), {
  polkit: {addRule: value => rule = value, Result: {YES: 'yes'}},
});
function authorize(user, unit, verb, id = 'org.freedesktop.systemd1.manage-units') {
  return rule({id, lookup: key => ({unit, verb})[key]}, {user});
}
test('only ARA may start/restart canonical guider unit', () => {
  for (const verb of ['start', 'restart']) {
    assert.equal(authorize('openastroara', 'openastro-guider.service', verb), 'yes');
  }
  for (const args of [
    ['other', 'openastro-guider.service', 'start'],
    ['openastroara', 'ssh.service', 'restart'],
    ['openastroara', 'openastro-guider.service', 'stop'],
    ['openastroara', 'openastro-guider.service', 'start', 'org.freedesktop.systemd1.manage-unit-files'],
    ['openastroara', 'openastro-phd2.service', 'start'],
  ]) assert.equal(authorize(...args), undefined);
});
