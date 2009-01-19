/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.IO;
using System.Reflection;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Environment.Interfaces;
using OpenSim.Region.Environment.Scenes;

namespace OpenSim.Data.MSSQL
{
    /// <summary>
    /// A MSSQL Interface for the Region Server.
    /// </summary>
    public class MSSQLRegionDataStore : IRegionDataStore
    {
        private const string _migrationStore = "RegionStore";

        // private static FileSystemDataStore Instance = new FileSystemDataStore();
        private static readonly ILog _Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The database manager
        /// </summary>
        private MSSQLManager _Database;
      
        /// <summary>
        /// Initialises the region datastore
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        public void Initialise(string connectionString)
        {
            if (!string.IsNullOrEmpty(connectionString))
            {
                _Database = new MSSQLManager(connectionString);
            }
            else
            {
                IniFile iniFile = new IniFile("mssql_connection.ini");
                string settingDataSource = iniFile.ParseFileReadValue("data_source");
                string settingInitialCatalog = iniFile.ParseFileReadValue("initial_catalog");
                string settingPersistSecurityInfo = iniFile.ParseFileReadValue("persist_security_info");
                string settingUserId = iniFile.ParseFileReadValue("user_id");
                string settingPassword = iniFile.ParseFileReadValue("password");

                _Database = new MSSQLManager(settingDataSource, settingInitialCatalog, settingPersistSecurityInfo, settingUserId, settingPassword);
            }

            //Migration settings
            _Database.CheckMigration(_migrationStore);

        }

        /// <summary>
        /// Dispose the database
        /// </summary>
        public void Dispose() { }

        #region SceneObjectGroup region for loading and Store of the scene.

        /// <summary>
        /// Loads the objects present in the region.
        /// </summary>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        public List<SceneObjectGroup> LoadObjects(UUID regionUUID)
        {
            UUID lastGroupID = UUID.Zero;

            List<SceneObjectPart> sceneObjectParts = new List<SceneObjectPart>();
            List<SceneObjectGroup> sceneObjectGroups = new List<SceneObjectGroup>();
            SceneObjectGroup grp = null;


            string query = "SELECT *, " +
                           "sort = CASE WHEN prims.UUID = prims.SceneGroupID THEN 0 ELSE 1 END " +
                           "FROM prims " +
                           "LEFT JOIN primshapes ON prims.UUID = primshapes.UUID " +
                           "WHERE RegionUUID = @RegionUUID " +
                           "ORDER BY SceneGroupID asc, sort asc, LinkNumber asc";

            using (AutoClosingSqlCommand command = _Database.Query(query))
            {
                command.Parameters.Add(_Database.CreateParameter("@regionUUID", regionUUID));

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        SceneObjectPart sceneObjectPart = BuildPrim(reader);
                        if (reader["Shape"] is DBNull)
                            sceneObjectPart.Shape = PrimitiveBaseShape.Default;
                        else
                            sceneObjectPart.Shape = BuildShape(reader);

                        sceneObjectPart.FolderID = sceneObjectPart.UUID; // A relic from when we
                        // we thought prims contained
                        // folder objects. In
                        // reality, prim == folder
                        sceneObjectParts.Add(sceneObjectPart);

                        UUID groupID = new UUID(reader["SceneGroupID"].ToString());

                        if (groupID != lastGroupID) // New SOG
                        {
                            if (grp != null)
                                sceneObjectGroups.Add(grp);

                            lastGroupID = groupID;

                            grp = new SceneObjectGroup(sceneObjectPart);
                        }
                        else
                        {
                            // Black magic to preserve link numbers
                            // Why is this needed, fix this in AddPart method.
                            int link = sceneObjectPart.LinkNum;

                            grp.AddPart(sceneObjectPart);

                            if (link != 0)
                                sceneObjectPart.LinkNum = link;
                        }
                    }
                }
            }

            if (grp != null)
                sceneObjectGroups.Add(grp);

            //Load the inventory off all sceneobjects within the region
            LoadItems(sceneObjectParts);

            _Log.DebugFormat("[DATABASE] Loaded {0} objects using {1} prims", sceneObjectGroups.Count, sceneObjectParts.Count);

            return sceneObjectGroups;

        }

        /// <summary>
        /// Load in the prim's persisted inventory.
        /// </summary>
        /// <param name="allPrims">all prims on a region</param>
        private void LoadItems(List<SceneObjectPart> allPrims)
        {
            using (AutoClosingSqlCommand command = _Database.Query("SELECT * FROM primitems WHERE PrimID = @PrimID"))
            {
                bool createParamOnce = true;

                foreach (SceneObjectPart objectPart in allPrims)
                {
                    if (createParamOnce)
                    {
                        command.Parameters.Add(_Database.CreateParameter("@PrimID", objectPart.UUID));
                        createParamOnce = false;
                    }
                    else
                    {
                        command.Parameters["@PrimID"].Value = objectPart.UUID.ToString();
                    }

                    List<TaskInventoryItem> inventory = new List<TaskInventoryItem>();

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            TaskInventoryItem item = BuildItem(reader);

                            item.ParentID = objectPart.UUID; // Values in database are
                            // often wrong
                            inventory.Add(item);
                        }
                    }

                    objectPart.Inventory.RestoreInventoryItems(inventory);
                }
            }
        }

        /// <summary>
        /// Stores all object's details apart from inventory
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="regionUUID"></param>
        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            _Log.InfoFormat("[REGION DB]: Adding/Changing SceneObjectGroup: {0} to region: {1}, object has {2} prims.", obj.UUID, regionUUID, obj.Children.Count);

            using (SqlConnection conn = _Database.DatabaseConnection())
            {
                SqlTransaction transaction = conn.BeginTransaction();

                try
                {
                    foreach (SceneObjectPart sceneObjectPart in obj.Children.Values)
                    {
                        //Update prim
                        using (SqlCommand sqlCommand = conn.CreateCommand())
                        {
                            sqlCommand.Transaction = transaction;
                            try
                            {
                                StoreSceneObjectPrim(sceneObjectPart, sqlCommand, obj.UUID, regionUUID);
                            }
                            catch (SqlException sqlEx)
                            {
                                _Log.ErrorFormat("[REGION DB]: Store SceneObjectPrim SQL error: {0} at line {1}", sqlEx.Message, sqlEx.LineNumber);
                                throw;
                            }
                        }

                        //Update primshapes
                        using (SqlCommand sqlCommand = conn.CreateCommand())
                        {
                            sqlCommand.Transaction = transaction;
                            try
                            {
                                StoreSceneObjectPrimShapes(sceneObjectPart, sqlCommand, obj.UUID, regionUUID);
                            }
                            catch (SqlException sqlEx)
                            {
                                _Log.ErrorFormat("[REGION DB]: Store SceneObjectPrimShapes SQL error: {0} at line {1}", sqlEx.Message, sqlEx.LineNumber);
                                throw;
                            }
                        }
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    _Log.ErrorFormat("[REGION DB]: Store SceneObjectGroup error: {0}, Rolling back...", ex.Message);
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception ex2)
                    {
                        //Show error
                        _Log.InfoFormat("[REGION DB]: Rollback of SceneObjectGroup store transaction failed with error: {0}", ex2.Message);

                    }
                }
            }

        }

        /// <summary>
        /// Stores the prim of the sceneobjectpart.
        /// </summary>
        /// <param name="sceneObjectPart">The sceneobjectpart or prim.</param>
        /// <param name="sqlCommand">The SQL command with the transaction.</param>
        /// <param name="sceneGroupID">The scenegroup UUID.</param>
        /// <param name="regionUUID">The region UUID.</param>
        private void StoreSceneObjectPrim(SceneObjectPart sceneObjectPart, SqlCommand sqlCommand, UUID sceneGroupID, UUID regionUUID)
        {
            //Big query to update or insert a new prim.
            //Note for SQL Server 2008 this could be simplified
            string queryPrims = @"
IF EXISTS (SELECT UUID FROM prims WHERE UUID = @UUID)
    BEGIN
        UPDATE prims SET 
            CreationDate = @CreationDate, Name = @Name, Text = @Text, Description = @Description, SitName = @SitName, 
            TouchName = @TouchName, ObjectFlags = @ObjectFlags, OwnerMask = @OwnerMask, NextOwnerMask = @NextOwnerMask, GroupMask = @GroupMask, 
            EveryoneMask = @EveryoneMask, BaseMask = @BaseMask, PositionX = @PositionX, PositionY = @PositionY, PositionZ = @PositionZ, 
            GroupPositionX = @GroupPositionX, GroupPositionY = @GroupPositionY, GroupPositionZ = @GroupPositionZ, VelocityX = @VelocityX, 
            VelocityY = @VelocityY, VelocityZ = @VelocityZ, AngularVelocityX = @AngularVelocityX, AngularVelocityY = @AngularVelocityY, 
            AngularVelocityZ = @AngularVelocityZ, AccelerationX = @AccelerationX, AccelerationY = @AccelerationY, 
            AccelerationZ = @AccelerationZ, RotationX = @RotationX, RotationY = @RotationY, RotationZ = @RotationZ, RotationW = @RotationW, 
            SitTargetOffsetX = @SitTargetOffsetX, SitTargetOffsetY = @SitTargetOffsetY, SitTargetOffsetZ = @SitTargetOffsetZ, 
            SitTargetOrientW = @SitTargetOrientW, SitTargetOrientX = @SitTargetOrientX, SitTargetOrientY = @SitTargetOrientY, 
            SitTargetOrientZ = @SitTargetOrientZ, RegionUUID = @RegionUUID, CreatorID = @CreatorID, OwnerID = @OwnerID, GroupID = @GroupID, 
            LastOwnerID = @LastOwnerID, SceneGroupID = @SceneGroupID, PayPrice = @PayPrice, PayButton1 = @PayButton1, PayButton2 = @PayButton2, 
            PayButton3 = @PayButton3, PayButton4 = @PayButton4, LoopedSound = @LoopedSound, LoopedSoundGain = @LoopedSoundGain, 
            TextureAnimation = @TextureAnimation, OmegaX = @OmegaX, OmegaY = @OmegaY, OmegaZ = @OmegaZ, CameraEyeOffsetX = @CameraEyeOffsetX, 
            CameraEyeOffsetY = @CameraEyeOffsetY, CameraEyeOffsetZ = @CameraEyeOffsetZ, CameraAtOffsetX = @CameraAtOffsetX, 
            CameraAtOffsetY = @CameraAtOffsetY, CameraAtOffsetZ = @CameraAtOffsetZ, ForceMouselook = @ForceMouselook, 
            ScriptAccessPin = @ScriptAccessPin, AllowedDrop = @AllowedDrop, DieAtEdge = @DieAtEdge, SalePrice = @SalePrice, 
            SaleType = @SaleType, ColorR = @ColorR, ColorG = @ColorG, ColorB = @ColorB, ColorA = @ColorA, ParticleSystem = @ParticleSystem, 
            ClickAction = @ClickAction, Material = @Material, CollisionSound = @CollisionSound, CollisionSoundVolume = @CollisionSoundVolume, 
            LinkNumber = @LinkNumber
        WHERE UUID = @UUID
    END
ELSE
    BEGIN
        INSERT INTO 
            prims (
            UUID, CreationDate, Name, Text, Description, SitName, TouchName, ObjectFlags, OwnerMask, NextOwnerMask, GroupMask, 
            EveryoneMask, BaseMask, PositionX, PositionY, PositionZ, GroupPositionX, GroupPositionY, GroupPositionZ, VelocityX, 
            VelocityY, VelocityZ, AngularVelocityX, AngularVelocityY, AngularVelocityZ, AccelerationX, AccelerationY, AccelerationZ, 
            RotationX, RotationY, RotationZ, RotationW, SitTargetOffsetX, SitTargetOffsetY, SitTargetOffsetZ, SitTargetOrientW, 
            SitTargetOrientX, SitTargetOrientY, SitTargetOrientZ, RegionUUID, CreatorID, OwnerID, GroupID, LastOwnerID, SceneGroupID, 
            PayPrice, PayButton1, PayButton2, PayButton3, PayButton4, LoopedSound, LoopedSoundGain, TextureAnimation, OmegaX, 
            OmegaY, OmegaZ, CameraEyeOffsetX, CameraEyeOffsetY, CameraEyeOffsetZ, CameraAtOffsetX, CameraAtOffsetY, CameraAtOffsetZ, 
            ForceMouselook, ScriptAccessPin, AllowedDrop, DieAtEdge, SalePrice, SaleType, ColorR, ColorG, ColorB, ColorA, 
            ParticleSystem, ClickAction, Material, CollisionSound, CollisionSoundVolume, LinkNumber
            ) VALUES (
            @UUID, @CreationDate, @Name, @Text, @Description, @SitName, @TouchName, @ObjectFlags, @OwnerMask, @NextOwnerMask, @GroupMask, 
            @EveryoneMask, @BaseMask, @PositionX, @PositionY, @PositionZ, @GroupPositionX, @GroupPositionY, @GroupPositionZ, @VelocityX, 
            @VelocityY, @VelocityZ, @AngularVelocityX, @AngularVelocityY, @AngularVelocityZ, @AccelerationX, @AccelerationY, @AccelerationZ, 
            @RotationX, @RotationY, @RotationZ, @RotationW, @SitTargetOffsetX, @SitTargetOffsetY, @SitTargetOffsetZ, @SitTargetOrientW, 
            @SitTargetOrientX, @SitTargetOrientY, @SitTargetOrientZ, @RegionUUID, @CreatorID, @OwnerID, @GroupID, @LastOwnerID, @SceneGroupID, 
            @PayPrice, @PayButton1, @PayButton2, @PayButton3, @PayButton4, @LoopedSound, @LoopedSoundGain, @TextureAnimation, @OmegaX, 
            @OmegaY, @OmegaZ, @CameraEyeOffsetX, @CameraEyeOffsetY, @CameraEyeOffsetZ, @CameraAtOffsetX, @CameraAtOffsetY, @CameraAtOffsetZ, 
            @ForceMouselook, @ScriptAccessPin, @AllowedDrop, @DieAtEdge, @SalePrice, @SaleType, @ColorR, @ColorG, @ColorB, @ColorA, 
            @ParticleSystem, @ClickAction, @Material, @CollisionSound, @CollisionSoundVolume, @LinkNumber
            )
    END";

            //Set commandtext.
            sqlCommand.CommandText = queryPrims;
            //Add parameters
            sqlCommand.Parameters.AddRange(CreatePrimParameters(sceneObjectPart, sceneGroupID, regionUUID));

            //Execute the query. If it fails then error is trapped in calling function
            sqlCommand.ExecuteNonQuery();
        }

        /// <summary>
        /// Stores the scene object prim shapes.
        /// </summary>
        /// <param name="sceneObjectPart">The sceneobjectpart containing prim shape.</param>
        /// <param name="sqlCommand">The SQL command with the transaction.</param>
        /// <param name="sceneGroupID">The scenegroup UUID.</param>
        /// <param name="regionUUID">The region UUID.</param>
        private void StoreSceneObjectPrimShapes(SceneObjectPart sceneObjectPart, SqlCommand sqlCommand, UUID sceneGroupID, UUID regionUUID)
        {
            //Big query to or insert or update primshapes
            //Note for SQL Server 2008 this can be simplified
            string queryPrimShapes = @"
IF EXISTS (SELECT UUID FROM primshapes WHERE UUID = @UUID)
    BEGIN
        UPDATE primshapes SET 
            Shape = @Shape, ScaleX = @ScaleX, ScaleY = @ScaleY, ScaleZ = @ScaleZ, PCode = @PCode, PathBegin = @PathBegin, 
            PathEnd = @PathEnd, PathScaleX = @PathScaleX, PathScaleY = @PathScaleY, PathShearX = @PathShearX, PathShearY = @PathShearY, 
            PathSkew = @PathSkew, PathCurve = @PathCurve, PathRadiusOffset = @PathRadiusOffset, PathRevolutions = @PathRevolutions, 
            PathTaperX = @PathTaperX, PathTaperY = @PathTaperY, PathTwist = @PathTwist, PathTwistBegin = @PathTwistBegin, 
            ProfileBegin = @ProfileBegin, ProfileEnd = @ProfileEnd, ProfileCurve = @ProfileCurve, ProfileHollow = @ProfileHollow, 
            Texture = @Texture, ExtraParams = @ExtraParams, State = @State
        WHERE UUID = @UUID
    END
ELSE
    BEGIN
        INSERT INTO 
            primshapes (
            UUID, Shape, ScaleX, ScaleY, ScaleZ, PCode, PathBegin, PathEnd, PathScaleX, PathScaleY, PathShearX, PathShearY, 
            PathSkew, PathCurve, PathRadiusOffset, PathRevolutions, PathTaperX, PathTaperY, PathTwist, PathTwistBegin, ProfileBegin, 
            ProfileEnd, ProfileCurve, ProfileHollow, Texture, ExtraParams, State
            ) VALUES (
            @UUID, @Shape, @ScaleX, @ScaleY, @ScaleZ, @PCode, @PathBegin, @PathEnd, @PathScaleX, @PathScaleY, @PathShearX, @PathShearY, 
            @PathSkew, @PathCurve, @PathRadiusOffset, @PathRevolutions, @PathTaperX, @PathTaperY, @PathTwist, @PathTwistBegin, @ProfileBegin, 
            @ProfileEnd, @ProfileCurve, @ProfileHollow, @Texture, @ExtraParams, @State
            )
    END";

            //Set commandtext.
            sqlCommand.CommandText = queryPrimShapes;

            //Add parameters
            sqlCommand.Parameters.AddRange(CreatePrimShapeParameters(sceneObjectPart, sceneGroupID, regionUUID));

            //Execute the query. If it fails then error is trapped in calling function
            sqlCommand.ExecuteNonQuery();

        }

        /// <summary>
        /// Removes a object from the database.
        /// Meaning removing it from tables Prims, PrimShapes and PrimItems
        /// </summary>
        /// <param name="objectID">id of scenegroup</param>
        /// <param name="regionUUID">regionUUID (is this used anyway</param>
        public void RemoveObject(UUID objectID, UUID regionUUID)
        {
            _Log.InfoFormat("[REGION DB]: Removing obj: {0} from region: {1}", objectID, regionUUID);

            //Remove from prims and primsitem table
            string sqlPrims = string.Format("DELETE FROM PRIMS WHERE SceneGroupID = '{0}'", objectID);
            string sqlPrimItems = string.Format("DELETE FROM PRIMITEMS WHERE primID in (SELECT UUID FROM PRIMS WHERE SceneGroupID = '{0}')", objectID);
            string sqlPrimShapes = string.Format("DELETE FROM PRIMSHAPES WHERE uuid in (SELECT UUID FROM PRIMS WHERE SceneGroupID = '{0}')", objectID);

            lock (_Database)
            {
                //Using the non transaction mode.
                using (AutoClosingSqlCommand cmd = _Database.Query(sqlPrimShapes))
                {
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = sqlPrimItems;
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = sqlPrims;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Store the inventory of a prim. Warning deletes everything first and then adds all again.
        /// </summary>
        /// <param name="primID"></param>
        /// <param name="items"></param>
        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
            //_Log.InfoFormat("[REGION DB: Persisting Prim Inventory with prim ID {0}", primID);

            //Statement from MySQL section!
            // For now, we're just going to crudely remove all the previous inventory items
            // no matter whether they have changed or not, and replace them with the current set.

            //Delete everything from PrimID
            //TODO add index on PrimID in DB, if not already exist
            using (AutoClosingSqlCommand cmd = _Database.Query("DELETE PRIMITEMS WHERE primID = @primID"))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@primID", primID));
                cmd.ExecuteNonQuery();
            }

            string sql =
                @"INSERT INTO primitems (
            itemID,primID,assetID,parentFolderID,invType,assetType,name,description,creationDate,creatorID,ownerID,lastOwnerID,groupID,
            nextPermissions,currentPermissions,basePermissions,everyonePermissions,groupPermissions,flags) 
            VALUES (@itemID,@primID,@assetID,@parentFolderID,@invType,@assetType,@name,@description,@creationDate,@creatorID,@ownerID,
            @lastOwnerID,@groupID,@nextPermissions,@currentPermissions,@basePermissions,@everyonePermissions,@groupPermissions,@flags)";

            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                foreach (TaskInventoryItem taskItem in items)
                {
                    cmd.Parameters.AddRange(CreatePrimInventoryParameters(taskItem));
                    cmd.ExecuteNonQuery();

                    cmd.Parameters.Clear();
                }
            }
        }

        #endregion

        /// <summary>
        /// Loads the terrain map.
        /// </summary>
        /// <param name="regionID">regionID.</param>
        /// <returns></returns>
        public double[,] LoadTerrain(UUID regionID)
        {
            double[,] terrain = new double[256, 256];
            terrain.Initialize();

            string sql = "select top 1 RegionUUID, Revision, Heightfield from terrain where RegionUUID = @RegionUUID order by Revision desc";

            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                // MySqlParameter param = new MySqlParameter();
                cmd.Parameters.AddWithValue("@RegionUUID", regionID.ToString());

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    int rev;
                    if (reader.Read())
                    {
                        MemoryStream str = new MemoryStream((byte[])reader["Heightfield"]);
                        BinaryReader br = new BinaryReader(str);
                        for (int x = 0; x < 256; x++)
                        {
                            for (int y = 0; y < 256; y++)
                            {
                                terrain[x, y] = br.ReadDouble();
                            }
                        }
                        rev = (int)reader["Revision"];
                    }
                    else
                    {
                        _Log.Info("[REGION DB]: No terrain found for region");
                        return null;
                    }
                    _Log.Info("[REGION DB]: Loaded terrain revision r" + rev);
                }
            }

            return terrain;
        }

        /// <summary>
        /// Stores the terrain map to DB.
        /// </summary>
        /// <param name="terrain">terrain map data.</param>
        /// <param name="regionID">regionID.</param>
        public void StoreTerrain(double[,] terrain, UUID regionID)
        {
            int revision = Util.UnixTimeSinceEpoch();

            //Delete old terrain map
            string sql = "delete from terrain where RegionUUID=@RegionUUID";
            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionID));
                cmd.ExecuteNonQuery();
            }

            sql = "insert into terrain(RegionUUID, Revision, Heightfield) values(@RegionUUID, @Revision, @Heightfield)";

            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionID));
                cmd.Parameters.Add(_Database.CreateParameter("@Revision", revision));
                cmd.Parameters.Add(_Database.CreateParameter("@Heightfield", serializeTerrain(terrain)));
                cmd.ExecuteNonQuery();
            }

            _Log.Info("[REGION DB]: Stored terrain revision r " + revision);
        }

        /// <summary>
        /// Loads all the land objects of a region.
        /// </summary>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            List<LandData> landDataForRegion = new List<LandData>();

            string sql = "select * from land where RegionUUID = @RegionUUID";

            //Retrieve all land data from region
            using (AutoClosingSqlCommand cmdLandData = _Database.Query(sql))
            {
                cmdLandData.Parameters.Add(_Database.CreateParameter("@RegionUUID", regionUUID));

                using (SqlDataReader readerLandData = cmdLandData.ExecuteReader())
                {
                    while (readerLandData.Read())
                    {
                        landDataForRegion.Add(BuildLandData(readerLandData));
                    }
                }
            }

            //Retrieve all accesslist data for all landdata
            foreach (LandData landData in landDataForRegion)
            {
                sql = "select * from landaccesslist where LandUUID = @LandUUID";
                using (AutoClosingSqlCommand cmdAccessList = _Database.Query(sql))
                {
                    cmdAccessList.Parameters.Add(_Database.CreateParameter("@LandUUID", landData.GlobalID));
                    using (SqlDataReader readerAccessList = cmdAccessList.ExecuteReader())
                    {
                        while (readerAccessList.Read())
                        {
                            landData.ParcelAccessList.Add(BuildLandAccessData(readerAccessList));
                        }
                    }
                }
            }

            //Return data
            return landDataForRegion;
        }

        /// <summary>
        /// Stores land object with landaccess list.
        /// </summary>
        /// <param name="parcel">parcel data.</param>
        public void StoreLandObject(ILandObject parcel)
        {
            //As this is only one record in land table I just delete all and then add a new record.
            //As the delete landaccess is already in the mysql code

            //Delete old values
            RemoveLandObject(parcel.landData.GlobalID);

            //Insert new values
            string sql = @"INSERT INTO [land] 
([UUID],[RegionUUID],[LocalLandID],[Bitmap],[Name],[Description],[OwnerUUID],[IsGroupOwned],[Area],[AuctionID],[Category],[ClaimDate],[ClaimPrice],[GroupUUID],[SalePrice],[LandStatus],[LandFlags],[LandingType],[MediaAutoScale],[MediaTextureUUID],[MediaURL],[MusicURL],[PassHours],[PassPrice],[SnapshotUUID],[UserLocationX],[UserLocationY],[UserLocationZ],[UserLookAtX],[UserLookAtY],[UserLookAtZ],[AuthbuyerID],[OtherCleanTime],[Dwell])
VALUES
(@UUID,@RegionUUID,@LocalLandID,@Bitmap,@Name,@Description,@OwnerUUID,@IsGroupOwned,@Area,@AuctionID,@Category,@ClaimDate,@ClaimPrice,@GroupUUID,@SalePrice,@LandStatus,@LandFlags,@LandingType,@MediaAutoScale,@MediaTextureUUID,@MediaURL,@MusicURL,@PassHours,@PassPrice,@SnapshotUUID,@UserLocationX,@UserLocationY,@UserLocationZ,@UserLookAtX,@UserLookAtY,@UserLookAtZ,@AuthbuyerID,@OtherCleanTime,@Dwell)";

            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                cmd.Parameters.AddRange(CreateLandParameters(parcel.landData, parcel.regionUUID));

                cmd.ExecuteNonQuery();
            }

            sql = "INSERT INTO [landaccesslist] ([LandUUID],[AccessUUID],[Flags]) VALUES (@LandUUID,@AccessUUID,@Flags)";

            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                foreach (ParcelManager.ParcelAccessEntry parcelAccessEntry in parcel.landData.ParcelAccessList)
                {
                    cmd.Parameters.AddRange(CreateLandAccessParameters(parcelAccessEntry, parcel.regionUUID));

                    cmd.ExecuteNonQuery();
                    cmd.Parameters.Clear();
                }
            }
        }

        /// <summary>
        /// Removes a land object from DB.
        /// </summary>
        /// <param name="globalID">UUID of landobject</param>
        public void RemoveLandObject(UUID globalID)
        {
            using (AutoClosingSqlCommand cmd = _Database.Query("delete from land where UUID=@UUID"))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@UUID", globalID));
                cmd.ExecuteNonQuery();
            }

            using (AutoClosingSqlCommand cmd = _Database.Query("delete from landaccesslist where LandUUID=@UUID"))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@UUID", globalID));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Loads the settings of a region.
        /// </summary>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        public RegionSettings LoadRegionSettings(UUID regionUUID)
        {
            string sql = "select * from regionsettings where regionUUID = @regionUUID";
            RegionSettings regionSettings;
            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@regionUUID", regionUUID));
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        regionSettings = BuildRegionSettings(reader);
                        regionSettings.OnSave += StoreRegionSettings;

                        return regionSettings;
                    }
                }
            }

            //If comes here then there is now region setting for that region
            regionSettings = new RegionSettings();
            regionSettings.RegionUUID = regionUUID;
            regionSettings.OnSave += StoreRegionSettings;

            //Store new values
            StoreNewRegionSettings(regionSettings);

            return regionSettings;
        }

        /// <summary>
        /// Store region settings, need to check if the check is really necesary. If we can make something for creating new region.
        /// </summary>
        /// <param name="regionSettings">region settings.</param>
        public void StoreRegionSettings(RegionSettings regionSettings)
        {
            //Little check if regionUUID already exist in DB
            string regionUUID;
            using (AutoClosingSqlCommand cmd = _Database.Query("SELECT regionUUID FROM regionsettings WHERE regionUUID = @regionUUID"))
            {
                cmd.Parameters.Add(_Database.CreateParameter("@regionUUID", regionSettings.RegionUUID));
                regionUUID = cmd.ExecuteScalar().ToString();
            }

            if (string.IsNullOrEmpty(regionUUID))
            {
                StoreNewRegionSettings(regionSettings);
            }
            else
            {
                //This method only updates region settings!!! First call LoadRegionSettings to create new region settings in DB
                string sql =
                    @"UPDATE [regionsettings] SET [block_terraform] = @block_terraform ,[block_fly] = @block_fly ,[allow_damage] = @allow_damage 
,[restrict_pushing] = @restrict_pushing ,[allow_land_resell] = @allow_land_resell ,[allow_land_join_divide] = @allow_land_join_divide 
,[block_show_in_search] = @block_show_in_search ,[agent_limit] = @agent_limit ,[object_bonus] = @object_bonus ,[maturity] = @maturity 
,[disable_scripts] = @disable_scripts ,[disable_collisions] = @disable_collisions ,[disable_physics] = @disable_physics 
,[terrain_texture_1] = @terrain_texture_1 ,[terrain_texture_2] = @terrain_texture_2 ,[terrain_texture_3] = @terrain_texture_3 
,[terrain_texture_4] = @terrain_texture_4 ,[elevation_1_nw] = @elevation_1_nw ,[elevation_2_nw] = @elevation_2_nw 
,[elevation_1_ne] = @elevation_1_ne ,[elevation_2_ne] = @elevation_2_ne ,[elevation_1_se] = @elevation_1_se ,[elevation_2_se] = @elevation_2_se 
,[elevation_1_sw] = @elevation_1_sw ,[elevation_2_sw] = @elevation_2_sw ,[water_height] = @water_height ,[terrain_raise_limit] = @terrain_raise_limit 
,[terrain_lower_limit] = @terrain_lower_limit ,[use_estate_sun] = @use_estate_sun ,[fixed_sun] = @fixed_sun ,[sun_position] = @sun_position 
,[covenant] = @covenant , [sunvectorx] = @sunvectorx, [sunvectory] = @sunvectory, [sunvectorz] = @sunvectorz,  [Sandbox] = @Sandbox WHERE [regionUUID] = @regionUUID";

                using (AutoClosingSqlCommand cmd = _Database.Query(sql))
                {
                    cmd.Parameters.AddRange(CreateRegionSettingParameters(regionSettings));

                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void Shutdown()
        {
            //Not used??
        }

        #region Private Methods

        /// <summary>
        /// Serializes the terrain data for storage in DB.
        /// </summary>
        /// <param name="val">terrain data</param>
        /// <returns></returns>
        private static Array serializeTerrain(double[,] val)
        {
            MemoryStream str = new MemoryStream(65536 * sizeof(double));
            BinaryWriter bw = new BinaryWriter(str);

            // TODO: COMPATIBILITY - Add byte-order conversions
            for (int x = 0; x < 256; x++)
                for (int y = 0; y < 256; y++)
                {
                    double height = val[x, y];
                    if (height == 0.0)
                        height = double.Epsilon;

                    bw.Write(height);
                }

            return str.ToArray();
        }

        /// <summary>
        /// Stores new regionsettings.
        /// </summary>
        /// <param name="regionSettings">The region settings.</param>
        private void StoreNewRegionSettings(RegionSettings regionSettings)
        {
            string sql = @"INSERT INTO [regionsettings]
                                ([regionUUID],[block_terraform],[block_fly],[allow_damage],[restrict_pushing],[allow_land_resell],[allow_land_join_divide],
                                [block_show_in_search],[agent_limit],[object_bonus],[maturity],[disable_scripts],[disable_collisions],[disable_physics],
                                [terrain_texture_1],[terrain_texture_2],[terrain_texture_3],[terrain_texture_4],[elevation_1_nw],[elevation_2_nw],[elevation_1_ne],
                                [elevation_2_ne],[elevation_1_se],[elevation_2_se],[elevation_1_sw],[elevation_2_sw],[water_height],[terrain_raise_limit],
                                [terrain_lower_limit],[use_estate_sun],[fixed_sun],[sun_position],[covenant],[sunvectorx], [sunvectory], [sunvectorz],[Sandbox]) 
                            VALUES
                                (@regionUUID,@block_terraform,@block_fly,@allow_damage,@restrict_pushing,@allow_land_resell,@allow_land_join_divide,
                                @block_show_in_search,@agent_limit,@object_bonus,@maturity,@disable_scripts,@disable_collisions,@disable_physics,
                                @terrain_texture_1,@terrain_texture_2,@terrain_texture_3,@terrain_texture_4,@elevation_1_nw,@elevation_2_nw,@elevation_1_ne,
                                @elevation_2_ne,@elevation_1_se,@elevation_2_se,@elevation_1_sw,@elevation_2_sw,@water_height,@terrain_raise_limit,
                                @terrain_lower_limit,@use_estate_sun,@fixed_sun,@sun_position,@covenant,@sunvectorx,@sunvectory, @sunvectorz, @Sandbox)";

            using (AutoClosingSqlCommand cmd = _Database.Query(sql))
            {
                cmd.Parameters.AddRange(CreateRegionSettingParameters(regionSettings));
                cmd.ExecuteNonQuery();
            }
        }

        #region Private DataRecord conversion methods

        /// <summary>
        /// Builds the region settings from a datarecod.
        /// </summary>
        /// <param name="row">datarecord with regionsettings.</param>
        /// <returns></returns>
        private static RegionSettings BuildRegionSettings(IDataRecord row)
        {
            //TODO change this is some more generic code so we doesnt have to change it every time a new field is added?
            RegionSettings newSettings = new RegionSettings();

            newSettings.RegionUUID = new UUID((string)row["regionUUID"]);
            newSettings.BlockTerraform = Convert.ToBoolean(row["block_terraform"]);
            newSettings.AllowDamage = Convert.ToBoolean(row["allow_damage"]);
            newSettings.BlockFly = Convert.ToBoolean(row["block_fly"]);
            newSettings.RestrictPushing = Convert.ToBoolean(row["restrict_pushing"]);
            newSettings.AllowLandResell = Convert.ToBoolean(row["allow_land_resell"]);
            newSettings.AllowLandJoinDivide = Convert.ToBoolean(row["allow_land_join_divide"]);
            newSettings.BlockShowInSearch = Convert.ToBoolean(row["block_show_in_search"]);
            newSettings.AgentLimit = Convert.ToInt32(row["agent_limit"]);
            newSettings.ObjectBonus = Convert.ToDouble(row["object_bonus"]);
            newSettings.Maturity = Convert.ToInt32(row["maturity"]);
            newSettings.DisableScripts = Convert.ToBoolean(row["disable_scripts"]);
            newSettings.DisableCollisions = Convert.ToBoolean(row["disable_collisions"]);
            newSettings.DisablePhysics = Convert.ToBoolean(row["disable_physics"]);
            newSettings.TerrainTexture1 = new UUID((String)row["terrain_texture_1"]);
            newSettings.TerrainTexture2 = new UUID((String)row["terrain_texture_2"]);
            newSettings.TerrainTexture3 = new UUID((String)row["terrain_texture_3"]);
            newSettings.TerrainTexture4 = new UUID((String)row["terrain_texture_4"]);
            newSettings.Elevation1NW = Convert.ToDouble(row["elevation_1_nw"]);
            newSettings.Elevation2NW = Convert.ToDouble(row["elevation_2_nw"]);
            newSettings.Elevation1NE = Convert.ToDouble(row["elevation_1_ne"]);
            newSettings.Elevation2NE = Convert.ToDouble(row["elevation_2_ne"]);
            newSettings.Elevation1SE = Convert.ToDouble(row["elevation_1_se"]);
            newSettings.Elevation2SE = Convert.ToDouble(row["elevation_2_se"]);
            newSettings.Elevation1SW = Convert.ToDouble(row["elevation_1_sw"]);
            newSettings.Elevation2SW = Convert.ToDouble(row["elevation_2_sw"]);
            newSettings.WaterHeight = Convert.ToDouble(row["water_height"]);
            newSettings.TerrainRaiseLimit = Convert.ToDouble(row["terrain_raise_limit"]);
            newSettings.TerrainLowerLimit = Convert.ToDouble(row["terrain_lower_limit"]);
            newSettings.UseEstateSun = Convert.ToBoolean(row["use_estate_sun"]);
            newSettings.Sandbox = Convert.ToBoolean(row["sandbox"]);
            newSettings.FixedSun = Convert.ToBoolean(row["fixed_sun"]);
            newSettings.SunPosition = Convert.ToDouble(row["sun_position"]);
            newSettings.SunVector = new Vector3(
                                                 Convert.ToSingle(row["sunvectorx"]),
                                                 Convert.ToSingle(row["sunvectory"]),
                                                 Convert.ToSingle(row["sunvectorz"])
                                                 );
            newSettings.Covenant = new UUID((String)row["covenant"]);

            return newSettings;
        }

        /// <summary>
        /// Builds the land data from a datarecord.
        /// </summary>
        /// <param name="row">datarecord with land data</param>
        /// <returns></returns>
        private static LandData BuildLandData(IDataRecord row)
        {
            LandData newData = new LandData();

            newData.GlobalID = new UUID((String)row["UUID"]);
            newData.LocalID = Convert.ToInt32(row["LocalLandID"]);

            // Bitmap is a byte[512]
            newData.Bitmap = (Byte[])row["Bitmap"];

            newData.Name = (String)row["Name"];
            newData.Description = (String)row["Description"];
            newData.OwnerID = (UUID)(String)row["OwnerUUID"];
            newData.IsGroupOwned = Convert.ToBoolean(row["IsGroupOwned"]);
            newData.Area = Convert.ToInt32(row["Area"]);
            newData.AuctionID = Convert.ToUInt32(row["AuctionID"]); //Unemplemented
            newData.Category = (Parcel.ParcelCategory)Convert.ToInt32(row["Category"]);
            //Enum libsecondlife.Parcel.ParcelCategory
            newData.ClaimDate = Convert.ToInt32(row["ClaimDate"]);
            newData.ClaimPrice = Convert.ToInt32(row["ClaimPrice"]);
            newData.GroupID = new UUID((String)row["GroupUUID"]);
            newData.SalePrice = Convert.ToInt32(row["SalePrice"]);
            newData.Status = (Parcel.ParcelStatus)Convert.ToInt32(row["LandStatus"]);
            //Enum. libsecondlife.Parcel.ParcelStatus
            newData.Flags = Convert.ToUInt32(row["LandFlags"]);
            newData.LandingType = Convert.ToByte(row["LandingType"]);
            newData.MediaAutoScale = Convert.ToByte(row["MediaAutoScale"]);
            newData.MediaID = new UUID((String)row["MediaTextureUUID"]);
            newData.MediaURL = (String)row["MediaURL"];
            newData.MusicURL = (String)row["MusicURL"];
            newData.PassHours = Convert.ToSingle(row["PassHours"]);
            newData.PassPrice = Convert.ToInt32(row["PassPrice"]);

            UUID authedbuyer;
            UUID snapshotID;

            if (UUID.TryParse((string)row["AuthBuyerID"], out authedbuyer))
                newData.AuthBuyerID = authedbuyer;

            if (UUID.TryParse((string)row["SnapshotUUID"], out snapshotID))
                newData.SnapshotID = snapshotID;

            newData.OtherCleanTime = Convert.ToInt32(row["OtherCleanTime"]);
            newData.Dwell = Convert.ToInt32(row["Dwell"]);

            try
            {
                newData.UserLocation =
                    new Vector3(Convert.ToSingle(row["UserLocationX"]), Convert.ToSingle(row["UserLocationY"]),
                                  Convert.ToSingle(row["UserLocationZ"]));
                newData.UserLookAt =
                    new Vector3(Convert.ToSingle(row["UserLookAtX"]), Convert.ToSingle(row["UserLookAtY"]),
                                  Convert.ToSingle(row["UserLookAtZ"]));
            }
            catch (InvalidCastException)
            {
                newData.UserLocation = Vector3.Zero;
                newData.UserLookAt = Vector3.Zero;
                _Log.ErrorFormat("[PARCEL]: unable to get parcel telehub settings for {1}", newData.Name);
            }

            newData.ParcelAccessList = new List<ParcelManager.ParcelAccessEntry>();

            return newData;
        }

        /// <summary>
        /// Builds the landaccess data from a data record.
        /// </summary>
        /// <param name="row">datarecord with landaccess data</param>
        /// <returns></returns>
        private static ParcelManager.ParcelAccessEntry BuildLandAccessData(IDataRecord row)
        {
            ParcelManager.ParcelAccessEntry entry = new ParcelManager.ParcelAccessEntry();
            entry.AgentID = new UUID((string)row["AccessUUID"]);
            entry.Flags = (AccessList)Convert.ToInt32(row["Flags"]);
            entry.Time = new DateTime();
            return entry;
        }

        /// <summary>
        /// Builds the prim from a datarecord.
        /// </summary>
        /// <param name="primRow">datarecord</param>
        /// <returns></returns>
        private static SceneObjectPart BuildPrim(IDataRecord primRow)
        {
            SceneObjectPart prim = new SceneObjectPart();

            prim.UUID = new UUID((String)primRow["UUID"]);
            // explicit conversion of integers is required, which sort
            // of sucks.  No idea if there is a shortcut here or not.
            prim.CreationDate = Convert.ToInt32(primRow["CreationDate"]);
            prim.Name = (String)primRow["Name"];
            // various text fields
            prim.Text = (String)primRow["Text"];
            prim.Color = Color.FromArgb(Convert.ToInt32(primRow["ColorA"]),
                                        Convert.ToInt32(primRow["ColorR"]),
                                        Convert.ToInt32(primRow["ColorG"]),
                                        Convert.ToInt32(primRow["ColorB"]));
            prim.Description = (String)primRow["Description"];
            prim.SitName = (String)primRow["SitName"];
            prim.TouchName = (String)primRow["TouchName"];
            // permissions
            prim.ObjectFlags = Convert.ToUInt32(primRow["ObjectFlags"]);
            prim.CreatorID = new UUID((String)primRow["CreatorID"]);
            prim.OwnerID = new UUID((String)primRow["OwnerID"]);
            prim.GroupID = new UUID((String)primRow["GroupID"]);
            prim.LastOwnerID = new UUID((String)primRow["LastOwnerID"]);
            prim.OwnerMask = Convert.ToUInt32(primRow["OwnerMask"]);
            prim.NextOwnerMask = Convert.ToUInt32(primRow["NextOwnerMask"]);
            prim.GroupMask = Convert.ToUInt32(primRow["GroupMask"]);
            prim.EveryoneMask = Convert.ToUInt32(primRow["EveryoneMask"]);
            prim.BaseMask = Convert.ToUInt32(primRow["BaseMask"]);
            // vectors
            prim.OffsetPosition = new Vector3(
                                    Convert.ToSingle(primRow["PositionX"]),
                                    Convert.ToSingle(primRow["PositionY"]),
                                    Convert.ToSingle(primRow["PositionZ"]));

            prim.GroupPosition = new Vector3(
                                    Convert.ToSingle(primRow["GroupPositionX"]),
                                    Convert.ToSingle(primRow["GroupPositionY"]),
                                    Convert.ToSingle(primRow["GroupPositionZ"]));

            prim.Velocity = new Vector3(
                                Convert.ToSingle(primRow["VelocityX"]),
                                Convert.ToSingle(primRow["VelocityY"]),
                                Convert.ToSingle(primRow["VelocityZ"]));

            prim.AngularVelocity = new Vector3(
                                    Convert.ToSingle(primRow["AngularVelocityX"]),
                                    Convert.ToSingle(primRow["AngularVelocityY"]),
                                    Convert.ToSingle(primRow["AngularVelocityZ"]));

            prim.Acceleration = new Vector3(
                                Convert.ToSingle(primRow["AccelerationX"]),
                                Convert.ToSingle(primRow["AccelerationY"]),
                                Convert.ToSingle(primRow["AccelerationZ"]));

            // quaternions
            prim.RotationOffset = new Quaternion(
                                Convert.ToSingle(primRow["RotationX"]),
                                Convert.ToSingle(primRow["RotationY"]),
                                Convert.ToSingle(primRow["RotationZ"]),
                                Convert.ToSingle(primRow["RotationW"]));

            prim.SitTargetPositionLL = new Vector3(
                                Convert.ToSingle(primRow["SitTargetOffsetX"]),
                                Convert.ToSingle(primRow["SitTargetOffsetY"]),
                                Convert.ToSingle(primRow["SitTargetOffsetZ"]));

            prim.SitTargetOrientationLL = new Quaternion(
                                Convert.ToSingle(primRow["SitTargetOrientX"]),
                                Convert.ToSingle(primRow["SitTargetOrientY"]),
                                Convert.ToSingle(primRow["SitTargetOrientZ"]),
                                Convert.ToSingle(primRow["SitTargetOrientW"]));

            prim.PayPrice[0] = Convert.ToInt32(primRow["PayPrice"]);
            prim.PayPrice[1] = Convert.ToInt32(primRow["PayButton1"]);
            prim.PayPrice[2] = Convert.ToInt32(primRow["PayButton2"]);
            prim.PayPrice[3] = Convert.ToInt32(primRow["PayButton3"]);
            prim.PayPrice[4] = Convert.ToInt32(primRow["PayButton4"]);

            prim.Sound = new UUID(primRow["LoopedSound"].ToString());
            prim.SoundGain = Convert.ToSingle(primRow["LoopedSoundGain"]);
            prim.SoundFlags = 1; // If it's persisted at all, it's looped

            if (!(primRow["TextureAnimation"] is DBNull))
                prim.TextureAnimation = (Byte[])primRow["TextureAnimation"];
            if (!(primRow["ParticleSystem"] is DBNull))
                prim.ParticleSystem = (Byte[])primRow["ParticleSystem"];

            prim.RotationalVelocity = new Vector3(
                                        Convert.ToSingle(primRow["OmegaX"]),
                                        Convert.ToSingle(primRow["OmegaY"]),
                                        Convert.ToSingle(primRow["OmegaZ"]));

            prim.SetCameraEyeOffset(new Vector3(
                                        Convert.ToSingle(primRow["CameraEyeOffsetX"]),
                                        Convert.ToSingle(primRow["CameraEyeOffsetY"]),
                                        Convert.ToSingle(primRow["CameraEyeOffsetZ"])
                                        ));

            prim.SetCameraAtOffset(new Vector3(
                                       Convert.ToSingle(primRow["CameraAtOffsetX"]),
                                       Convert.ToSingle(primRow["CameraAtOffsetY"]),
                                       Convert.ToSingle(primRow["CameraAtOffsetZ"])
                                       ));

            if (Convert.ToInt16(primRow["ForceMouselook"]) != 0)
                prim.SetForceMouselook(true);

            prim.ScriptAccessPin = Convert.ToInt32(primRow["ScriptAccessPin"]);

            if (Convert.ToInt16(primRow["AllowedDrop"]) != 0)
                prim.AllowedDrop = true;

            if (Convert.ToInt16(primRow["DieAtEdge"]) != 0)
                prim.DIE_AT_EDGE = true;

            prim.SalePrice = Convert.ToInt32(primRow["SalePrice"]);
            prim.ObjectSaleType = Convert.ToByte(primRow["SaleType"]);

            prim.Material = Convert.ToByte(primRow["Material"]);

            if (!(primRow["ClickAction"] is DBNull))
                prim.ClickAction = Convert.ToByte(primRow["ClickAction"]);

            prim.CollisionSound = new UUID(primRow["CollisionSound"].ToString());
            prim.CollisionSoundVolume = Convert.ToSingle(primRow["CollisionSoundVolume"]);

            prim.LinkNum = Convert.ToInt32(primRow["LinkNumber"]);

            return prim;
        }

        /// <summary>
        /// Builds the prim shape from a datarecord.
        /// </summary>
        /// <param name="shapeRow">The row.</param>
        /// <returns></returns>
        private static PrimitiveBaseShape BuildShape(IDataRecord shapeRow)
        {
            PrimitiveBaseShape baseShape = new PrimitiveBaseShape();

            baseShape.Scale = new Vector3(
                        Convert.ToSingle(shapeRow["ScaleX"]),
                        Convert.ToSingle(shapeRow["ScaleY"]),
                        Convert.ToSingle(shapeRow["ScaleZ"]));

            // paths
            baseShape.PCode = Convert.ToByte(shapeRow["PCode"]);
            baseShape.PathBegin = Convert.ToUInt16(shapeRow["PathBegin"]);
            baseShape.PathEnd = Convert.ToUInt16(shapeRow["PathEnd"]);
            baseShape.PathScaleX = Convert.ToByte(shapeRow["PathScaleX"]);
            baseShape.PathScaleY = Convert.ToByte(shapeRow["PathScaleY"]);
            baseShape.PathShearX = Convert.ToByte(shapeRow["PathShearX"]);
            baseShape.PathShearY = Convert.ToByte(shapeRow["PathShearY"]);
            baseShape.PathSkew = Convert.ToSByte(shapeRow["PathSkew"]);
            baseShape.PathCurve = Convert.ToByte(shapeRow["PathCurve"]);
            baseShape.PathRadiusOffset = Convert.ToSByte(shapeRow["PathRadiusOffset"]);
            baseShape.PathRevolutions = Convert.ToByte(shapeRow["PathRevolutions"]);
            baseShape.PathTaperX = Convert.ToSByte(shapeRow["PathTaperX"]);
            baseShape.PathTaperY = Convert.ToSByte(shapeRow["PathTaperY"]);
            baseShape.PathTwist = Convert.ToSByte(shapeRow["PathTwist"]);
            baseShape.PathTwistBegin = Convert.ToSByte(shapeRow["PathTwistBegin"]);
            // profile
            baseShape.ProfileBegin = Convert.ToUInt16(shapeRow["ProfileBegin"]);
            baseShape.ProfileEnd = Convert.ToUInt16(shapeRow["ProfileEnd"]);
            baseShape.ProfileCurve = Convert.ToByte(shapeRow["ProfileCurve"]);
            baseShape.ProfileHollow = Convert.ToUInt16(shapeRow["ProfileHollow"]);

            byte[] textureEntry = (byte[])shapeRow["Texture"];
            baseShape.TextureEntry = textureEntry;

            baseShape.ExtraParams = (byte[])shapeRow["ExtraParams"];

            try
            {
                baseShape.State = Convert.ToByte(shapeRow["State"]);
            }
            catch (InvalidCastException)
            {
            }

            return baseShape;
        }

        /// <summary>
        /// Build a prim inventory item from the persisted data.
        /// </summary>
        /// <param name="inventoryRow"></param>
        /// <returns></returns>
        private static TaskInventoryItem BuildItem(IDataRecord inventoryRow)
        {
            TaskInventoryItem taskItem = new TaskInventoryItem();

            taskItem.ItemID = new UUID((String)inventoryRow["itemID"]);
            taskItem.ParentPartID = new UUID((String)inventoryRow["primID"]);
            taskItem.AssetID = new UUID((String)inventoryRow["assetID"]);
            taskItem.ParentID = new UUID((String)inventoryRow["parentFolderID"]);

            taskItem.InvType = Convert.ToInt32(inventoryRow["invType"]);
            taskItem.Type = Convert.ToInt32(inventoryRow["assetType"]);

            taskItem.Name = (String)inventoryRow["name"];
            taskItem.Description = (String)inventoryRow["description"];
            taskItem.CreationDate = Convert.ToUInt32(inventoryRow["creationDate"]);
            taskItem.CreatorID = new UUID((String)inventoryRow["creatorID"]);
            taskItem.OwnerID = new UUID((String)inventoryRow["ownerID"]);
            taskItem.LastOwnerID = new UUID((String)inventoryRow["lastOwnerID"]);
            taskItem.GroupID = new UUID((String)inventoryRow["groupID"]);

            taskItem.NextPermissions = Convert.ToUInt32(inventoryRow["nextPermissions"]);
            taskItem.CurrentPermissions = Convert.ToUInt32(inventoryRow["currentPermissions"]);
            taskItem.BasePermissions = Convert.ToUInt32(inventoryRow["basePermissions"]);
            taskItem.EveryonePermissions = Convert.ToUInt32(inventoryRow["everyonePermissions"]);
            taskItem.GroupPermissions = Convert.ToUInt32(inventoryRow["groupPermissions"]);
            taskItem.Flags = Convert.ToUInt32(inventoryRow["flags"]);

            return taskItem;
        }

        #endregion

        #region Create parameters methods
        
        /// <summary>
        /// Creates the prim inventory parameters.
        /// </summary>
        /// <param name="taskItem">item in inventory.</param>
        /// <returns></returns>
        private SqlParameter[] CreatePrimInventoryParameters(TaskInventoryItem taskItem)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("itemID", taskItem.ItemID));
            parameters.Add(_Database.CreateParameter("primID", taskItem.ParentPartID));
            parameters.Add(_Database.CreateParameter("assetID", taskItem.AssetID));
            parameters.Add(_Database.CreateParameter("parentFolderID", taskItem.ParentID));
            parameters.Add(_Database.CreateParameter("invType", taskItem.InvType));
            parameters.Add(_Database.CreateParameter("assetType", taskItem.Type));

            parameters.Add(_Database.CreateParameter("name", taskItem.Name));
            parameters.Add(_Database.CreateParameter("description", taskItem.Description));
            parameters.Add(_Database.CreateParameter("creationDate", taskItem.CreationDate));
            parameters.Add(_Database.CreateParameter("creatorID", taskItem.CreatorID));
            parameters.Add(_Database.CreateParameter("ownerID", taskItem.OwnerID));
            parameters.Add(_Database.CreateParameter("lastOwnerID", taskItem.LastOwnerID));
            parameters.Add(_Database.CreateParameter("groupID", taskItem.GroupID));
            parameters.Add(_Database.CreateParameter("nextPermissions", taskItem.NextPermissions));
            parameters.Add(_Database.CreateParameter("currentPermissions", taskItem.CurrentPermissions));
            parameters.Add(_Database.CreateParameter("basePermissions", taskItem.BasePermissions));
            parameters.Add(_Database.CreateParameter("everyonePermissions", taskItem.EveryonePermissions));
            parameters.Add(_Database.CreateParameter("groupPermissions", taskItem.GroupPermissions));
            parameters.Add(_Database.CreateParameter("flags", taskItem.Flags));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the region setting parameters.
        /// </summary>
        /// <param name="settings">regionsettings.</param>
        /// <returns></returns>
        private SqlParameter[] CreateRegionSettingParameters(RegionSettings settings)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("regionUUID", settings.RegionUUID));
            parameters.Add(_Database.CreateParameter("block_terraform", settings.BlockTerraform));
            parameters.Add(_Database.CreateParameter("block_fly", settings.BlockFly));
            parameters.Add(_Database.CreateParameter("allow_damage", settings.AllowDamage));
            parameters.Add(_Database.CreateParameter("restrict_pushing", settings.RestrictPushing));
            parameters.Add(_Database.CreateParameter("allow_land_resell", settings.AllowLandResell));
            parameters.Add(_Database.CreateParameter("allow_land_join_divide", settings.AllowLandJoinDivide));
            parameters.Add(_Database.CreateParameter("block_show_in_search", settings.BlockShowInSearch));
            parameters.Add(_Database.CreateParameter("agent_limit", settings.AgentLimit));
            parameters.Add(_Database.CreateParameter("object_bonus", settings.ObjectBonus));
            parameters.Add(_Database.CreateParameter("maturity", settings.Maturity));
            parameters.Add(_Database.CreateParameter("disable_scripts", settings.DisableScripts));
            parameters.Add(_Database.CreateParameter("disable_collisions", settings.DisableCollisions));
            parameters.Add(_Database.CreateParameter("disable_physics", settings.DisablePhysics));
            parameters.Add(_Database.CreateParameter("terrain_texture_1", settings.TerrainTexture1));
            parameters.Add(_Database.CreateParameter("terrain_texture_2", settings.TerrainTexture2));
            parameters.Add(_Database.CreateParameter("terrain_texture_3", settings.TerrainTexture3));
            parameters.Add(_Database.CreateParameter("terrain_texture_4", settings.TerrainTexture4));
            parameters.Add(_Database.CreateParameter("elevation_1_nw", settings.Elevation1NW));
            parameters.Add(_Database.CreateParameter("elevation_2_nw", settings.Elevation2NW));
            parameters.Add(_Database.CreateParameter("elevation_1_ne", settings.Elevation1NE));
            parameters.Add(_Database.CreateParameter("elevation_2_ne", settings.Elevation2NE));
            parameters.Add(_Database.CreateParameter("elevation_1_se", settings.Elevation1SE));
            parameters.Add(_Database.CreateParameter("elevation_2_se", settings.Elevation2SE));
            parameters.Add(_Database.CreateParameter("elevation_1_sw", settings.Elevation1SW));
            parameters.Add(_Database.CreateParameter("elevation_2_sw", settings.Elevation2SW));
            parameters.Add(_Database.CreateParameter("water_height", settings.WaterHeight));
            parameters.Add(_Database.CreateParameter("terrain_raise_limit", settings.TerrainRaiseLimit));
            parameters.Add(_Database.CreateParameter("terrain_lower_limit", settings.TerrainLowerLimit));
            parameters.Add(_Database.CreateParameter("use_estate_sun", settings.UseEstateSun));
            parameters.Add(_Database.CreateParameter("sandbox", settings.Sandbox));
            parameters.Add(_Database.CreateParameter("fixed_sun", settings.FixedSun));
            parameters.Add(_Database.CreateParameter("sun_position", settings.SunPosition));
            parameters.Add(_Database.CreateParameter("sunvectorx", settings.SunVector.X));
            parameters.Add(_Database.CreateParameter("sunvectory", settings.SunVector.Y));
            parameters.Add(_Database.CreateParameter("sunvectorz", settings.SunVector.Z));
            parameters.Add(_Database.CreateParameter("covenant", settings.Covenant));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the land parameters.
        /// </summary>
        /// <param name="land">land parameters.</param>
        /// <param name="regionUUID">region UUID.</param>
        /// <returns></returns>
        private SqlParameter[] CreateLandParameters(LandData land, UUID regionUUID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("UUID", land.GlobalID));
            parameters.Add(_Database.CreateParameter("RegionUUID", regionUUID));
            parameters.Add(_Database.CreateParameter("LocalLandID", land.LocalID));

            // Bitmap is a byte[512]
            parameters.Add(_Database.CreateParameter("Bitmap", land.Bitmap));

            parameters.Add(_Database.CreateParameter("Name", land.Name));
            parameters.Add(_Database.CreateParameter("Description", land.Description));
            parameters.Add(_Database.CreateParameter("OwnerUUID", land.OwnerID));
            parameters.Add(_Database.CreateParameter("IsGroupOwned", land.IsGroupOwned));
            parameters.Add(_Database.CreateParameter("Area", land.Area));
            parameters.Add(_Database.CreateParameter("AuctionID", land.AuctionID)); //Unemplemented
            parameters.Add(_Database.CreateParameter("Category", (int)land.Category)); //Enum libsecondlife.Parcel.ParcelCategory
            parameters.Add(_Database.CreateParameter("ClaimDate", land.ClaimDate));
            parameters.Add(_Database.CreateParameter("ClaimPrice", land.ClaimPrice));
            parameters.Add(_Database.CreateParameter("GroupUUID", land.GroupID));
            parameters.Add(_Database.CreateParameter("SalePrice", land.SalePrice));
            parameters.Add(_Database.CreateParameter("LandStatus", (int)land.Status)); //Enum. libsecondlife.Parcel.ParcelStatus
            parameters.Add(_Database.CreateParameter("LandFlags", land.Flags));
            parameters.Add(_Database.CreateParameter("LandingType", land.LandingType));
            parameters.Add(_Database.CreateParameter("MediaAutoScale", land.MediaAutoScale));
            parameters.Add(_Database.CreateParameter("MediaTextureUUID", land.MediaID));
            parameters.Add(_Database.CreateParameter("MediaURL", land.MediaURL));
            parameters.Add(_Database.CreateParameter("MusicURL", land.MusicURL));
            parameters.Add(_Database.CreateParameter("PassHours", land.PassHours));
            parameters.Add(_Database.CreateParameter("PassPrice", land.PassPrice));
            parameters.Add(_Database.CreateParameter("SnapshotUUID", land.SnapshotID));
            parameters.Add(_Database.CreateParameter("UserLocationX", land.UserLocation.X));
            parameters.Add(_Database.CreateParameter("UserLocationY", land.UserLocation.Y));
            parameters.Add(_Database.CreateParameter("UserLocationZ", land.UserLocation.Z));
            parameters.Add(_Database.CreateParameter("UserLookAtX", land.UserLookAt.X));
            parameters.Add(_Database.CreateParameter("UserLookAtY", land.UserLookAt.Y));
            parameters.Add(_Database.CreateParameter("UserLookAtZ", land.UserLookAt.Z));
            parameters.Add(_Database.CreateParameter("AuthBuyerID", land.AuthBuyerID));
            parameters.Add(_Database.CreateParameter("OtherCleanTime", land.OtherCleanTime));
            parameters.Add(_Database.CreateParameter("Dwell", land.Dwell));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the land access parameters.
        /// </summary>
        /// <param name="parcelAccessEntry">parcel access entry.</param>
        /// <param name="parcelID">parcel ID.</param>
        /// <returns></returns>
        private SqlParameter[] CreateLandAccessParameters(ParcelManager.ParcelAccessEntry parcelAccessEntry, UUID parcelID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("LandUUID", parcelID));
            parameters.Add(_Database.CreateParameter("AccessUUID", parcelAccessEntry.AgentID));
            parameters.Add(_Database.CreateParameter("Flags", parcelAccessEntry.Flags));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the prim parameters for storing in DB.
        /// </summary>
        /// <param name="prim">Basic data of SceneObjectpart prim.</param>
        /// <param name="sceneGroupID">The scenegroup ID.</param>
        /// <param name="regionUUID">The region ID.</param>
        /// <returns></returns>
        private SqlParameter[] CreatePrimParameters(SceneObjectPart prim, UUID sceneGroupID, UUID regionUUID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            parameters.Add(_Database.CreateParameter("UUID", prim.UUID));
            parameters.Add(_Database.CreateParameter("RegionUUID", regionUUID));
            parameters.Add(_Database.CreateParameter("CreationDate", prim.CreationDate));
            parameters.Add(_Database.CreateParameter("Name", prim.Name));
            parameters.Add(_Database.CreateParameter("SceneGroupID", sceneGroupID));
            // the UUID of the root part for this SceneObjectGroup
            // various text fields
            parameters.Add(_Database.CreateParameter("Text", prim.Text));
            parameters.Add(_Database.CreateParameter("ColorR", prim.Color.R));
            parameters.Add(_Database.CreateParameter("ColorG", prim.Color.G));
            parameters.Add(_Database.CreateParameter("ColorB", prim.Color.B));
            parameters.Add(_Database.CreateParameter("ColorA", prim.Color.A));
            parameters.Add(_Database.CreateParameter("Description", prim.Description));
            parameters.Add(_Database.CreateParameter("SitName", prim.SitName));
            parameters.Add(_Database.CreateParameter("TouchName", prim.TouchName));
            // permissions
            parameters.Add(_Database.CreateParameter("ObjectFlags", prim.ObjectFlags));
            parameters.Add(_Database.CreateParameter("CreatorID", prim.CreatorID));
            parameters.Add(_Database.CreateParameter("OwnerID", prim.OwnerID));
            parameters.Add(_Database.CreateParameter("GroupID", prim.GroupID));
            parameters.Add(_Database.CreateParameter("LastOwnerID", prim.LastOwnerID));
            parameters.Add(_Database.CreateParameter("OwnerMask", prim.OwnerMask));
            parameters.Add(_Database.CreateParameter("NextOwnerMask", prim.NextOwnerMask));
            parameters.Add(_Database.CreateParameter("GroupMask", prim.GroupMask));
            parameters.Add(_Database.CreateParameter("EveryoneMask", prim.EveryoneMask));
            parameters.Add(_Database.CreateParameter("BaseMask", prim.BaseMask));
            // vectors
            parameters.Add(_Database.CreateParameter("PositionX", prim.OffsetPosition.X));
            parameters.Add(_Database.CreateParameter("PositionY", prim.OffsetPosition.Y));
            parameters.Add(_Database.CreateParameter("PositionZ", prim.OffsetPosition.Z));
            parameters.Add(_Database.CreateParameter("GroupPositionX", prim.GroupPosition.X));
            parameters.Add(_Database.CreateParameter("GroupPositionY", prim.GroupPosition.Y));
            parameters.Add(_Database.CreateParameter("GroupPositionZ", prim.GroupPosition.Z));
            parameters.Add(_Database.CreateParameter("VelocityX", prim.Velocity.X));
            parameters.Add(_Database.CreateParameter("VelocityY", prim.Velocity.Y));
            parameters.Add(_Database.CreateParameter("VelocityZ", prim.Velocity.Z));
            parameters.Add(_Database.CreateParameter("AngularVelocityX", prim.AngularVelocity.X));
            parameters.Add(_Database.CreateParameter("AngularVelocityY", prim.AngularVelocity.Y));
            parameters.Add(_Database.CreateParameter("AngularVelocityZ", prim.AngularVelocity.Z));
            parameters.Add(_Database.CreateParameter("AccelerationX", prim.Acceleration.X));
            parameters.Add(_Database.CreateParameter("AccelerationY", prim.Acceleration.Y));
            parameters.Add(_Database.CreateParameter("AccelerationZ", prim.Acceleration.Z));
            // quaternions
            parameters.Add(_Database.CreateParameter("RotationX", prim.RotationOffset.X));
            parameters.Add(_Database.CreateParameter("RotationY", prim.RotationOffset.Y));
            parameters.Add(_Database.CreateParameter("RotationZ", prim.RotationOffset.Z));
            parameters.Add(_Database.CreateParameter("RotationW", prim.RotationOffset.W));

            // Sit target
            Vector3 sitTargetPos = prim.SitTargetPositionLL;
            parameters.Add(_Database.CreateParameter("SitTargetOffsetX", sitTargetPos.X));
            parameters.Add(_Database.CreateParameter("SitTargetOffsetY", sitTargetPos.Y));
            parameters.Add(_Database.CreateParameter("SitTargetOffsetZ", sitTargetPos.Z));

            Quaternion sitTargetOrient = prim.SitTargetOrientationLL;
            parameters.Add(_Database.CreateParameter("SitTargetOrientW", sitTargetOrient.W));
            parameters.Add(_Database.CreateParameter("SitTargetOrientX", sitTargetOrient.X));
            parameters.Add(_Database.CreateParameter("SitTargetOrientY", sitTargetOrient.Y));
            parameters.Add(_Database.CreateParameter("SitTargetOrientZ", sitTargetOrient.Z));

            parameters.Add(_Database.CreateParameter("PayPrice", prim.PayPrice[0]));
            parameters.Add(_Database.CreateParameter("PayButton1", prim.PayPrice[1]));
            parameters.Add(_Database.CreateParameter("PayButton2", prim.PayPrice[2]));
            parameters.Add(_Database.CreateParameter("PayButton3", prim.PayPrice[3]));
            parameters.Add(_Database.CreateParameter("PayButton4", prim.PayPrice[4]));

            if ((prim.SoundFlags & 1) != 0) // Looped
            {
                parameters.Add(_Database.CreateParameter("LoopedSound", prim.Sound));
                parameters.Add(_Database.CreateParameter("LoopedSoundGain", prim.SoundGain));
            }
            else
            {
                parameters.Add(_Database.CreateParameter("LoopedSound", UUID.Zero));
                parameters.Add(_Database.CreateParameter("LoopedSoundGain", 0.0f));
            }

            parameters.Add(_Database.CreateParameter("TextureAnimation", prim.TextureAnimation));
            parameters.Add(_Database.CreateParameter("ParticleSystem", prim.ParticleSystem));

            parameters.Add(_Database.CreateParameter("OmegaX", prim.RotationalVelocity.X));
            parameters.Add(_Database.CreateParameter("OmegaY", prim.RotationalVelocity.Y));
            parameters.Add(_Database.CreateParameter("OmegaZ", prim.RotationalVelocity.Z));

            parameters.Add(_Database.CreateParameter("CameraEyeOffsetX", prim.GetCameraEyeOffset().X));
            parameters.Add(_Database.CreateParameter("CameraEyeOffsetY", prim.GetCameraEyeOffset().Y));
            parameters.Add(_Database.CreateParameter("CameraEyeOffsetZ", prim.GetCameraEyeOffset().Z));

            parameters.Add(_Database.CreateParameter("CameraAtOffsetX", prim.GetCameraAtOffset().X));
            parameters.Add(_Database.CreateParameter("CameraAtOffsetY", prim.GetCameraAtOffset().Y));
            parameters.Add(_Database.CreateParameter("CameraAtOffsetZ", prim.GetCameraAtOffset().Z));

            if (prim.GetForceMouselook())
                parameters.Add(_Database.CreateParameter("ForceMouselook", 1));
            else
                parameters.Add(_Database.CreateParameter("ForceMouselook", 0));

            parameters.Add(_Database.CreateParameter("ScriptAccessPin", prim.ScriptAccessPin));

            if (prim.AllowedDrop)
                parameters.Add(_Database.CreateParameter("AllowedDrop", 1));
            else
                parameters.Add(_Database.CreateParameter("AllowedDrop", 0));

            if (prim.DIE_AT_EDGE)
                parameters.Add(_Database.CreateParameter("DieAtEdge", 1));
            else
                parameters.Add(_Database.CreateParameter("DieAtEdge", 0));

            parameters.Add(_Database.CreateParameter("SalePrice", prim.SalePrice));
            parameters.Add(_Database.CreateParameter("SaleType", prim.ObjectSaleType));

            byte clickAction = prim.ClickAction;
            parameters.Add(_Database.CreateParameter("ClickAction", clickAction));

            parameters.Add(_Database.CreateParameter("Material", prim.Material));

            parameters.Add(_Database.CreateParameter("CollisionSound", prim.CollisionSound));
            parameters.Add(_Database.CreateParameter("CollisionSoundVolume", prim.CollisionSoundVolume));
            parameters.Add(_Database.CreateParameter("LinkNumber", prim.LinkNum));

            return parameters.ToArray();
        }

        /// <summary>
        /// Creates the primshape parameters for stroing in DB.
        /// </summary>
        /// <param name="prim">Basic data of SceneObjectpart prim.</param>
        /// <param name="sceneGroupID">The scene group ID.</param>
        /// <param name="regionUUID">The region UUID.</param>
        /// <returns></returns>
        private SqlParameter[] CreatePrimShapeParameters(SceneObjectPart prim, UUID sceneGroupID, UUID regionUUID)
        {
            List<SqlParameter> parameters = new List<SqlParameter>();

            PrimitiveBaseShape s = prim.Shape;
            parameters.Add(_Database.CreateParameter("UUID", prim.UUID));
            // shape is an enum
            parameters.Add(_Database.CreateParameter("Shape", 0));
            // vectors
            parameters.Add(_Database.CreateParameter("ScaleX", s.Scale.X));
            parameters.Add(_Database.CreateParameter("ScaleY", s.Scale.Y));
            parameters.Add(_Database.CreateParameter("ScaleZ", s.Scale.Z));
            // paths
            parameters.Add(_Database.CreateParameter("PCode", s.PCode));
            parameters.Add(_Database.CreateParameter("PathBegin", s.PathBegin));
            parameters.Add(_Database.CreateParameter("PathEnd", s.PathEnd));
            parameters.Add(_Database.CreateParameter("PathScaleX", s.PathScaleX));
            parameters.Add(_Database.CreateParameter("PathScaleY", s.PathScaleY));
            parameters.Add(_Database.CreateParameter("PathShearX", s.PathShearX));
            parameters.Add(_Database.CreateParameter("PathShearY", s.PathShearY));
            parameters.Add(_Database.CreateParameter("PathSkew", s.PathSkew));
            parameters.Add(_Database.CreateParameter("PathCurve", s.PathCurve));
            parameters.Add(_Database.CreateParameter("PathRadiusOffset", s.PathRadiusOffset));
            parameters.Add(_Database.CreateParameter("PathRevolutions", s.PathRevolutions));
            parameters.Add(_Database.CreateParameter("PathTaperX", s.PathTaperX));
            parameters.Add(_Database.CreateParameter("PathTaperY", s.PathTaperY));
            parameters.Add(_Database.CreateParameter("PathTwist", s.PathTwist));
            parameters.Add(_Database.CreateParameter("PathTwistBegin", s.PathTwistBegin));
            // profile
            parameters.Add(_Database.CreateParameter("ProfileBegin", s.ProfileBegin));
            parameters.Add(_Database.CreateParameter("ProfileEnd", s.ProfileEnd));
            parameters.Add(_Database.CreateParameter("ProfileCurve", s.ProfileCurve));
            parameters.Add(_Database.CreateParameter("ProfileHollow", s.ProfileHollow));
            parameters.Add(_Database.CreateParameter("Texture", s.TextureEntry));
            parameters.Add(_Database.CreateParameter("ExtraParams", s.ExtraParams));
            parameters.Add(_Database.CreateParameter("State", s.State));

            return parameters.ToArray();
        }

    

        #endregion

        #endregion
    }
}
