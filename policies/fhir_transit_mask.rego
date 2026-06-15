# Transit-time masking policy for FHIR resources.
#
# Used by Service A's OpaMaskingHandler (DelegatingHandler) to mask sensitive
# fields BEFORE the resource is POSTed to the next service over the wire.
#
# IMPORTANT: transforms here MUST keep the resource a VALID FHIR document.
# That rules out string-replacement transforms ("mask"/"redact") on
# type-strict primitives such as `date`, `instant`, `decimal`, `code`, `uri`.
# For those we use "remove" (drop the optional field) instead.
#
# Compare with `fhir.log_mask` (fhir_log_mask.rego), which is the stricter
# policy used at LOG time inside Service B's Serilog destructuring policy
# where the output is plain text and FHIR validity no longer matters.
#
# Input contract:
#   input.resource — the FHIR resource as JSON
#
# Output contract:
#   data.fhir.transit_mask.decision = {
#       "transforms": [ { "path": "/name/0/family", "op": "hash" }, ... ]
#   }
package fhir.transit_mask

decision = {"transforms": all_transforms}

all_transforms = array.concat(patient_transforms,
                  array.concat(encounter_transforms, observation_transforms))

# ---------------------------------------------------------------------------
# Patient
# ---------------------------------------------------------------------------
patient_transforms = ts {
	input.resource.resourceType == "Patient"
	ts := array.concat(p_identifier,
	      array.concat(p_family,
	      array.concat(p_given,
	      array.concat(p_phone,
	      array.concat(p_email,
	      array.concat(p_birthdate,
	      array.concat(p_address_line, p_postal)))))))
}

patient_transforms = [] { input.resource.resourceType != "Patient" }

# String-typed -> hash keeps the field a string, FHIR stays valid.
p_identifier = [t |
	some i
	input.resource.identifier[i].value
	t := {"path": sprintf("/identifier/%d/value", [i]), "op": "hash"}
]

p_family = [t |
	some i
	input.resource.name[i].family
	t := {"path": sprintf("/name/%d/family", [i]), "op": "hash"}
]

p_given = [t |
	some i, j
	input.resource.name[i].given[j]
	t := {"path": sprintf("/name/%d/given/%d", [i, j]), "op": "mask"}
]

p_phone = [t |
	some i
	input.resource.telecom[i].system == "phone"
	t := {"path": sprintf("/telecom/%d/value", [i]), "op": "mask"}
]

p_email = [t |
	some i
	input.resource.telecom[i].system == "email"
	t := {"path": sprintf("/telecom/%d/value", [i]), "op": "hash"}
]

# birthDate is a FHIR `date` -> "REDACTED" is not a valid date literal.
# For transit we drop the optional field instead.
p_birthdate = [{"path": "/birthDate", "op": "remove"}] {
	input.resource.birthDate
}
p_birthdate = [] { not input.resource.birthDate }

p_address_line = [t |
	some i
	input.resource.address[i].line
	t := {"path": sprintf("/address/%d/line", [i]), "op": "remove"}
]

p_postal = [t |
	some i
	input.resource.address[i].postalCode
	t := {"path": sprintf("/address/%d/postalCode", [i]), "op": "mask"}
]

# ---------------------------------------------------------------------------
# Encounter
# ---------------------------------------------------------------------------
encounter_transforms = ts {
	input.resource.resourceType == "Encounter"
	ts := array.concat(e_subject_display,
	      array.concat(e_subject_ref, e_reason))
}

encounter_transforms = [] { input.resource.resourceType != "Encounter" }

# subject.display is a free-text string -> "REDACTED" is FHIR-valid.
e_subject_display = [{"path": "/subject/display", "op": "redact"}] {
	input.resource.subject.display
}
e_subject_display = [] { not input.resource.subject.display }

e_subject_ref = [{"path": "/subject/reference", "op": "hash"}] {
	input.resource.subject.reference
}
e_subject_ref = [] { not input.resource.subject.reference }

e_reason = [t |
	some i
	input.resource.reasonCode[i]
	t := {"path": sprintf("/reasonCode/%d", [i]), "op": "remove"}
]

# ---------------------------------------------------------------------------
# Observation
# ---------------------------------------------------------------------------
observation_transforms = ts {
	input.resource.resourceType == "Observation"
	ts := array.concat(o_subject_ref, o_subject_display)
}

observation_transforms = [] { input.resource.resourceType != "Observation" }

o_subject_ref = [{"path": "/subject/reference", "op": "hash"}] {
	input.resource.subject.reference
}
o_subject_ref = [] { not input.resource.subject.reference }

o_subject_display = [{"path": "/subject/display", "op": "redact"}] {
	input.resource.subject.display
}
o_subject_display = [] { not input.resource.subject.display }
