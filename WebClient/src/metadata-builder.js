function compactStrings(values) {
    if (!Array.isArray(values)) {
        return [];
    }

    return values.filter((value) => typeof value === 'string' && value.trim().length > 0);
}

export function buildVersionedMetadata(fileName, versionLabel, meta) {
    if (meta?.metaVersion === '0') {
        return {
            fileName,
            vrmVersion: versionLabel,
            metaVersion: '0',
            title: meta.title ?? '',
            authors: meta.author ? compactStrings([meta.author]) : [],
            modelVersion: meta.version ?? '',
            contactInformation: meta.contactInformation ?? '',
            reference: meta.reference ?? '',
            allowedUserName: meta.allowedUserName ?? '',
            violentUssageName: meta.violentUssageName ?? '',
            sexualUssageName: meta.sexualUssageName ?? '',
            commercialUssageName: meta.commercialUssageName ?? '',
            licenseName: meta.licenseName ?? '',
            otherPermissionUrl: meta.otherPermissionUrl ?? '',
            otherLicenseUrl: meta.otherLicenseUrl ?? ''
        };
    }

    if (meta?.metaVersion === '1') {
        return {
            fileName,
            vrmVersion: versionLabel,
            metaVersion: '1',
            title: meta.name ?? '',
            authors: compactStrings(meta.authors),
            modelVersion: meta.version ?? '',
            copyrightInformation: meta.copyrightInformation ?? '',
            contactInformation: meta.contactInformation ?? '',
            references: compactStrings(meta.references),
            thirdPartyLicenses: meta.thirdPartyLicenses ?? '',
            licenseUrl: meta.licenseUrl ?? '',
            avatarPermission: meta.avatarPermission ?? '',
            allowExcessivelyViolentUsage: meta.allowExcessivelyViolentUsage,
            allowExcessivelySexualUsage: meta.allowExcessivelySexualUsage,
            commercialUsage: meta.commercialUsage ?? '',
            allowPoliticalOrReligiousUsage: meta.allowPoliticalOrReligiousUsage,
            allowAntisocialOrHateUsage: meta.allowAntisocialOrHateUsage,
            creditNotation: meta.creditNotation ?? '',
            allowRedistribution: meta.allowRedistribution,
            modification: meta.modification ?? '',
            otherLicenseUrl: meta.otherLicenseUrl ?? ''
        };
    }

    throw new Error('three-vrm did not provide supported VRM metadata.');
}
