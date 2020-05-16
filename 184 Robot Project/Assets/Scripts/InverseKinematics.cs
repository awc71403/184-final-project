﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

//This system does NOT assume that all joint transforms are similarly aligned to the model (in other words, we don't assume that, for example, the local x-axis always points to the next joint).
//However, if this assumption can be made, then every joint's align vector is the same, and we can optimize.

//TODO - potential next steps:
//represent alignment as a rotation w.r.t. Vector3.forward
//re-implement base constraints using an origin
//optimize constrain_spin by moving reorient calls outside
//if the above step allows, move reorient into the end of get_direction/constrain_direction and make these a member function of Joint
//Fix spazzy bug

public enum JointType
{
    free,
    hinge
}

public enum Axis
{
    x,
    y,
    z
}

[Serializable]
public class Joint
{
    public JointType type;

    public Transform transform;

    //Constraints for orientation in degrees, from -180 to 180
    public float phiMin, phiMax;

    //Hinge joints only
    public Axis axis;
    public float thetaMin, thetaMax;//-180 to 180

    [NonSerialized]
    public Vector3 position;
    [NonSerialized]
    public Quaternion rotation;
    [NonSerialized]
    public Vector3 align;
    [NonSerialized]
    public float length;

    //Reorients to a specific direction
    public void reorient(Vector3 dir)
    {
        rotation = Quaternion.FromToRotation(rotation * align, dir) * rotation;
    }

    //TODO: for now, constrain_spin does all the thinking about different alignments
    //Hence this does NOT assume that either joint is parallel to the given axis, even though they are sometimes
    //But eventually we might optimize...
    public void constrain_spin(Joint parent, Vector3 axis)
    {
        Quaternion q = parent.rotation * Quaternion.FromToRotation(align, parent.align);
        q = Quaternion.FromToRotation(q * align, axis) * q;
        float angle;
        Vector3 angle_axis;
        reorient(axis);
        (rotation * Quaternion.Inverse(q)).ToAngleAxis(out angle, out angle_axis);
        if (Vector3.Dot(axis, angle_axis) < 0f) angle = -angle;
        rotation = Quaternion.AngleAxis(Mathf.Clamp(angle, phiMin, phiMax), axis) * q;
    }
}

public class InverseKinematics : MonoBehaviour
{
    [SerializeField]
    private List<Joint> joints;

    [SerializeField]
    private Transform pointer;//Some point to tell us how the end effector is aligned

    [SerializeField]
    private Transform target;

    private float tolerance;

    void Awake()
    {
        Vector3 dir;
        float total_length = 0f;
        for (int i = 0; i < joints.Count - 1; ++i)
        {
            Joint j = joints[i];
            dir = joints[i + 1].transform.position - j.transform.position;
            float length = dir.magnitude;
            j.length = length;
            j.align = j.transform.InverseTransformDirection(dir / length);
            total_length += length;
        }
        dir = pointer.position - joints[joints.Count - 1].transform.position;
        joints[joints.Count - 1].align = joints[joints.Count - 1].transform.InverseTransformDirection(dir.normalized);

        tolerance = 0.1f * total_length;
    }

    void Update()
    {
        fabrik_solve();
    }

    private void fabrik_solve() {
        //Initialize variables
        foreach (Joint j in joints)
        {
            j.position = j.transform.position;
            j.rotation = j.transform.rotation;
        }
        
        int num_loops = 0;
        float dif = float.PositiveInfinity;
        while (dif > tolerance)
        {
            //First pass: end to base
            joints[joints.Count - 1].position = target.position;
            joints[joints.Count - 1].rotation = target.rotation;

            Joint j, j_prev;
            Vector3 dir;
            for (int i = joints.Count - 2; i > 0; --i)
            {
                j = joints[i];
                j_prev = joints[i + 1];
                dir = get_direction(j_prev, j.position, true);
                j.position = j_prev.position + (j.length / dir.magnitude) * dir;
                j.constrain_spin(j_prev, -dir);
            }

            //Second pass: base to end
            j = joints[0];
            j_prev = joints[1];
            dir = -get_direction(j_prev, j.position, true);
            j.reorient(dir);
            for (int i = 1; i < joints.Count - 1; ++i)
            {
                j = joints[i];
                j_prev = joints[i - 1];
                j.position = j_prev.position + (j_prev.length / dir.magnitude) * dir;
                j.constrain_spin(j_prev, dir);
                dir = get_direction(j, joints[i + 1].position);
                j.reorient(dir);
            }

            //End effector
            int n = joints.Count - 1;
            j = joints[n];
            j_prev = joints[n - 1];
            j.position = j_prev.position + (j_prev.length / dir.magnitude) * dir;
            j.constrain_spin(j_prev, dir);
            dir = constrain_direction(j, target.rotation * j.align);
            j.reorient(dir);

            dif = Math.Abs(Vector3.Distance(j.position, target.position));
            ++num_loops;
            if (num_loops > 100)
            {
                Debug.Log("IK took too long to converge!");
                break;
            }
        }

        //Update transforms
        for (int i = 0; i < joints.Count; ++i)
        {
            joints[i].transform.rotation = joints[i].rotation;
        }
    }

    //Constrains the direction vector between from and to, according to from's constrants
    private Vector3 get_direction(Joint from, Vector3 to, bool reverse=false)
    {
        return constrain_direction(from, to - from.position, reverse);
    }

    private Vector3 constrain_direction(Joint j, Vector3 dir, bool reverse=false)
    {
        if (j.type == JointType.hinge)
        {
            Vector3 n = j.rotation * ((j.axis == Axis.x) ? Vector3.right :
                                         (j.axis == Axis.y) ? Vector3.up :
                                                                 Vector3.forward);
            dir -= Vector3.Dot(dir, n) * n;// Planar projection
            Vector3 straight = j.rotation * j.align;
            if (reverse) straight = -straight;
            float min = (reverse) ? -j.thetaMax : j.thetaMin;
            float max = (reverse) ? -j.thetaMin : j.thetaMax;
            float angle = Mathf.Clamp(Vector3.SignedAngle(straight, dir, n), min, max);
            return Quaternion.AngleAxis(angle, n) * straight;
        }
        return dir;
    }
}