import assert from 'node:assert/strict';
import test from 'node:test';
import { buildVersionedMetadata } from '../src/metadata-builder.js';

test('VRM 1.0 metadata includes only 1.0 fields and removes empty array entries', () => {
    const metadata = buildVersionedMetadata('avatar.vrm', 'VRM 1.0', {
        metaVersion: '1', name: 'Avatar', authors: ['Alice', '', '  ', null, 'Bob'], version: '1.2',
        copyrightInformation: 'Copyright', contactInformation: 'contact', references: ['First', '', 'Second'],
        thirdPartyLicenses: 'Third party', licenseUrl: 'https://vrm.dev/licenses/1.0/', avatarPermission: 'onlyAuthor',
        allowExcessivelyViolentUsage: false, allowExcessivelySexualUsage: true, commercialUsage: 'personalNonProfit',
        allowPoliticalOrReligiousUsage: false, allowAntisocialOrHateUsage: true, creditNotation: 'required',
        allowRedistribution: false, modification: 'allowModification', otherLicenseUrl: 'https://example.com/license'
    });

    assert.deepEqual(metadata, {
        fileName: 'avatar.vrm', vrmVersion: 'VRM 1.0', metaVersion: '1', title: 'Avatar',
        authors: ['Alice', 'Bob'], modelVersion: '1.2', copyrightInformation: 'Copyright',
        contactInformation: 'contact', references: ['First', 'Second'], thirdPartyLicenses: 'Third party',
        licenseUrl: 'https://vrm.dev/licenses/1.0/', avatarPermission: 'onlyAuthor',
        allowExcessivelyViolentUsage: false, allowExcessivelySexualUsage: true,
        commercialUsage: 'personalNonProfit', allowPoliticalOrReligiousUsage: false,
        allowAntisocialOrHateUsage: true, creditNotation: 'required', allowRedistribution: false,
        modification: 'allowModification', otherLicenseUrl: 'https://example.com/license'
    });
    assert.equal('allowedUserName' in metadata, false);
});

test('VRM 0.x metadata includes only 0.x fields and preserves specification spelling', () => {
    const metadata = buildVersionedMetadata('avatar.vrm', 'VRM 0.x', {
        metaVersion: '0', title: 'Avatar', author: 'Alice', version: '0.9', contactInformation: 'contact',
        reference: 'reference', allowedUserName: 'OnlyAuthor', violentUssageName: 'Disallow',
        sexualUssageName: 'Allow', commercialUssageName: 'Disallow', licenseName: 'CC_BY',
        otherPermissionUrl: 'https://example.com/permission', otherLicenseUrl: 'https://example.com/license'
    });

    assert.deepEqual(metadata, {
        fileName: 'avatar.vrm', vrmVersion: 'VRM 0.x', metaVersion: '0', title: 'Avatar',
        authors: ['Alice'], modelVersion: '0.9', contactInformation: 'contact', reference: 'reference',
        allowedUserName: 'OnlyAuthor', violentUssageName: 'Disallow', sexualUssageName: 'Allow',
        commercialUssageName: 'Disallow', licenseName: 'CC_BY',
        otherPermissionUrl: 'https://example.com/permission', otherLicenseUrl: 'https://example.com/license'
    });
    assert.equal('avatarPermission' in metadata, false);
});

test('missing values remain representable and unsupported metadata is rejected', () => {
    const metadata = buildVersionedMetadata('avatar.vrm', 'VRM 1.0', {
        metaVersion: '1', name: null, authors: null, references: ['', '  ']
    });

    assert.equal(metadata.title, '');
    assert.deepEqual(metadata.authors, []);
    assert.deepEqual(metadata.references, []);
    assert.equal(metadata.allowRedistribution, undefined);
    assert.throws(
        () => buildVersionedMetadata('avatar.vrm', 'Unknown', { metaVersion: '2' }),
        /supported VRM metadata/
    );
});
